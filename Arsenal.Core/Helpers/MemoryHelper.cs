using System.Runtime.InteropServices;

namespace Arsenal.Helpers
{
    public static class MemoryHelper
    {
        private const string DiagnosticsArgument = "--memory-diagnostics";

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMin, nint dwMax);

        /// <summary>
        /// Whether the expensive process-map and native-heap report was explicitly
        /// requested for this run.
        /// </summary>
        /// <remarks>
        /// Enumerating every module and address-space region is diagnostic work, not a
        /// memory-reduction technique. More importantly, walking native heaps requires
        /// locking each heap while it is inspected. Keep that work out of ordinary
        /// window toggles and tray cleanup while retaining the exact report for a run
        /// started with <c>--memory-diagnostics</c>.
        /// </remarks>
        public static bool DetailedReportingEnabled => Environment.GetCommandLineArgs()
            .Any(argument => argument.Equals(DiagnosticsArgument, StringComparison.OrdinalIgnoreCase));

        public static void TrimAfter(
            Task? prerequisite = null,
            TimeSpan? timeout = null,
            Func<bool>? shouldRun = null)
        {
            Task.Run(async () =>
            {
                if (prerequisite != null)
                {
                    try
                    {
                        await prerequisite.WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
                    }
                    catch { }
                }

                if (shouldRun is not null && !shouldRun()) return;
                Trim();
            });
        }

        /// <summary>
        /// Collects and gives the memory back, at the one moment it is free to be slow.
        /// </summary>
        /// <remarks>
        /// This used to ask with <c>GCCollectionMode.Optimized</c>, which is a request
        /// rather than an instruction: it lets the runtime decide that collecting is not
        /// worth it and do nothing. Measured on a tray return, it was doing nothing - the
        /// managed heap read 45MB before the release and 46MB after it, having dropped
        /// every cached page in between.
        ///
        /// <para>Aggressive, blocking and compacting instead, with the large object heap
        /// compacted in the same pass. That is the expensive form, and this is the one
        /// place that can afford it: no window is on screen, nothing has asked for
        /// anything for five seconds, and a pause nobody is present for costs nothing.
        /// Compacting is what lets whole segments go back to Windows rather than leaving
        /// a heap full of holes that still counts against the process.</para>
        ///
        /// <para>The working set is trimmed afterwards, not before: trimming first only
        /// pages out memory that the collection is about to make free anyway, and the
        /// pages come straight back.</para>
        /// </remarks>
        private static void Trim()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                // A second pass, because the first one runs finalizers and anything they
                // released is only collectable now.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Memory collect: " + ex.Message);
            }

            TrimWorkingSet();
        }

        /// <summary>
        /// Hands the resident pages back to Windows without collecting anything.
        /// </summary>
        /// <remarks>
        /// Cheap and interruptible, unlike the collection above: nothing is moved and no
        /// thread is stopped, the pages are simply no longer claimed and come back if
        /// they are touched. That makes it the only part of this that is safe to run
        /// while a window is on screen.
        /// </remarks>
        public static void TrimWorkingSet()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(p.Handle, -1, -1);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Working set trim: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes what this process is actually holding to the log.
        /// </summary>
        /// <remarks>
        /// From inside the process because there is no way to ask from outside it. This
        /// application re-elevates, and an elevated process answers a standard-user shell
        /// with an empty module list and no path - so Task Manager's one number is all
        /// anybody outside can see, and that number cannot say whether it is the managed
        /// heap, the graphics drivers or a leak.
        ///
        /// <para>Split the way the question needs answering: what the CLR is holding,
        /// what the process has committed, and what the loaded libraries account for. On
        /// a dual-GPU laptop the third of those is most of it, because WPF renders
        /// through D3D and both vendors' user-mode drivers load.</para>
        /// </remarks>
        public static void LogReport(string moment)
        {
            if (!DetailedReportingEnabled) return;

            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                p.Refresh();

                GCMemoryInfo gc = GC.GetGCMemoryInfo();

                long modules = 0;
                int count = 0;
                var biggest = new List<(string Name, long Size)>();
                try
                {
                    foreach (System.Diagnostics.ProcessModule module in p.Modules)
                    {
                        count++;
                        modules += module.ModuleMemorySize;
                        biggest.Add((module.ModuleName, module.ModuleMemorySize));
                    }
                }
                catch { /* a module list can be refused mid-walk; the totals still stand */ }

                Logger.WriteLine($"Memory [{moment}]: "
                    + $"managed {GC.GetTotalMemory(false) / (1024 * 1024)}MB, "
                    + $"gcCommitted {gc.TotalCommittedBytes / (1024 * 1024)}MB, "
                    + $"heapCount {gc.HeapSizeBytes / (1024 * 1024)}MB, "
                    + $"private {p.PrivateMemorySize64 / (1024 * 1024)}MB, "
                    + $"workingSet {p.WorkingSet64 / (1024 * 1024)}MB, "
                    + $"modules {count} totalling {modules / (1024 * 1024)}MB, "
                    + $"handles {p.HandleCount}, threads {p.Threads.Count}");

                foreach ((string name, long size) in biggest.OrderByDescending(m => m.Size).Take(12))
                    Logger.WriteLine($"    {size / (1024 * 1024),4}MB  {name}  (mapped image, mostly shared)");

                LogRegions();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Memory report failed: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------------
        // Where the committed memory actually is
        // -------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        [DllImport("kernel32.dll")]
        private static extern nuint VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

        [DllImport("kernel32.dll")]
        private static extern uint GetProcessHeaps(uint count, [Out] IntPtr[] heaps);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HeapLock(IntPtr heap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HeapUnlock(IntPtr heap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HeapWalk(IntPtr heap, ref PROCESS_HEAP_ENTRY entry);

        /// <remarks>
        /// The tail of this is a union, and the region arm of it is the one being read:
        /// two DWORDs then two pointers. Declaring it as pointer-sized fields instead
        /// would fold the two DWORDs into one and every committed size would be wrong -
        /// wrong in a way that still compiles, still runs and still prints numbers.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_HEAP_ENTRY
        {
            public IntPtr lpData;
            public uint cbData;
            public byte cbOverhead;
            public byte iRegionIndex;
            public ushort wFlags;
            public uint dwCommittedSize;
            public uint dwUnCommittedSize;
            public IntPtr lpFirstBlock;
            public IntPtr lpLastBlock;
        }

        private const ushort ProcessHeapRegion = 0x0001;

        /// <summary>The longest any one heap may be held locked, as Stopwatch ticks.</summary>
        private static readonly long BudgetTicks = System.Diagnostics.Stopwatch.Frequency / 20;

        /// <summary>
        /// Every native heap in the process, and how much each one is holding.
        /// </summary>
        /// <remarks>
        /// The region walk can name a library only when the memory is part of its mapped
        /// image. Everything a program allocates at runtime is anonymous - the walk sees
        /// a twenty-nine megabyte block and cannot say whose it is - and on this process
        /// that anonymous remainder is most of the number.
        ///
        /// <para>Native heaps are the one category that can still be attributed, because
        /// Windows will name them on request. If the large blocks turn out to be heaps,
        /// they belong to something in this program and can be chased. If they do not,
        /// they are the graphics driver's own allocations and nothing in this codebase
        /// can return them.</para>
        ///
        /// <para>Walked under <c>HeapLock</c>, which is what makes this safe to run in a
        /// live process: without it another thread allocating mid-walk invalidates the
        /// enumeration. Nothing is logged or allocated inside the lock - the totals go
        /// into an array sized before it is taken - because writing to the log would
        /// allocate from the very heap being held.</para>
        /// </remarks>
        private static List<(IntPtr Handle, long Committed, int Regions)> WalkHeaps()
        {
            var found = new List<(IntPtr, long, int)>();

            var handles = new IntPtr[256];
            uint count = GetProcessHeaps((uint)handles.Length, handles);
            if (count == 0) return found;
            if (count > handles.Length) count = (uint)handles.Length;

            // Sized before any lock is taken, so the walk itself never allocates.
            var sizes = new long[count];
            var regions = new int[count];

            for (int i = 0; i < count; i++)
            {
                IntPtr heap = handles[i];
                if (heap == IntPtr.Zero) continue;

                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                bool locked = false;
                try
                {
                    locked = HeapLock(heap);
                    if (!locked) continue;

                    var entry = new PROCESS_HEAP_ENTRY();
                    long committed = 0;
                    int seen = 0;
                    int steps = 0;

                    // HeapWalk has no filter: reaching the region entries means stepping
                    // over every individual allocation in between, and there can be
                    // hundreds of thousands of them. That happens with the heap locked,
                    // which stops every other thread that tries to allocate - so this is
                    // on a budget it cannot exceed. A partial count is worth having; a
                    // frozen application to get an exact one is not.
                    long until = start + BudgetTicks;

                    while (HeapWalk(heap, ref entry))
                    {
                        if ((++steps & 0x3FF) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() > until) break;

                        // Region entries describe the reserved segments themselves; the
                        // per-allocation entries in between would count the same bytes
                        // again.
                        if ((entry.wFlags & ProcessHeapRegion) == 0) continue;

                        committed += entry.dwCommittedSize;
                        seen++;
                        if (seen > 4096) break;
                    }

                    sizes[i] = committed;
                    regions[i] = seen;
                }
                catch { /* a heap can be destroyed underneath the walk; the rest still count */ }
                finally
                {
                    if (locked) HeapUnlock(heap);
                }
            }

            for (int i = 0; i < count; i++)
                if (sizes[i] > 0) found.Add((handles[i], sizes[i], regions[i]));

            return found;
        }

        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemImage = 0x1000000;
        private const uint MemMapped = 0x40000;

        private const uint PageReadWrite = 0x04;
        private const uint PageWriteCopy = 0x08;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageExecuteWriteCopy = 0x80;
        private const uint PageExecute = 0x10;
        private const uint PageExecuteRead = 0x20;
        private const uint PageGuard = 0x100;

        /// <summary>
        /// Walks the address space and says where the committed memory is.
        /// </summary>
        /// <remarks>
        /// The totals above cannot answer the only question worth asking, which is what
        /// to cut. They report a managed heap of about fifteen megabytes and five hundred
        /// megabytes of loaded libraries, and neither figure is the cost: the heap is too
        /// small to matter and a mapped library is shared between every process that
        /// loaded it, so it is charged to the machine rather than to us. That leaves the
        /// difference - most of the number in Task Manager - completely unattributed.
        ///
        /// <para>This walks every region the process owns and sorts the committed bytes
        /// three ways. <b>Image</b> is a loaded library, split into the read-only and
        /// executable pages that every process shares and the writable data pages that
        /// are ours alone - that split is what shows a hundred and sixty megabytes of
        /// graphics driver costing us almost nothing. <b>Mapped</b> is a file or a shared
        /// section, which is where compositor surfaces tend to land. <b>Private</b> is
        /// everything we allocated: the managed heap, native heaps, thread stacks,
        /// compiled code, and the driver's own working memory.</para>
        ///
        /// <para>Private is the part that can actually be reduced, so it is broken down
        /// again by what the pages are for and the largest single allocations are named.
        /// A hundred-megabyte block with no owner is worth finding.</para>
        ///
        /// <para>Reserved-but-not-committed is reported separately and is not a cost.
        /// It is mostly thread stacks, which reserve a megabyte each and commit a few
        /// pages, and the GC reserving room it has not needed yet.</para>
        /// </remarks>
        public static void LogRegions()
        {
            try
            {
                var moduleAt = new Dictionary<long, string>();
                try
                {
                    using var p = System.Diagnostics.Process.GetCurrentProcess();
                    foreach (System.Diagnostics.ProcessModule module in p.Modules)
                        moduleAt[module.BaseAddress.ToInt64()] = module.ModuleName;
                }
                catch { /* without the map the regions are still counted, just unnamed */ }

                long imageShared = 0, imagePrivate = 0, mapped = 0;
                long privateWritable = 0, privateExecutable = 0, privateOther = 0;
                long reserved = 0;
                int regions = 0;

                var perModule = new Dictionary<string, long>();
                var blocks = new List<(long Size, ulong At, string What)>();
                var currentBlock = new Dictionary<ulong, long>();

                ulong address = 0;
                nuint size = (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

                while (VirtualQuery((IntPtr)(long)address, out MEMORY_BASIC_INFORMATION region, size) != 0)
                {
                    ulong length = (ulong)region.RegionSize;
                    if (length == 0) break;

                    if (region.State == MemReserve) reserved += (long)length;

                    if (region.State == MemCommit)
                    {
                        regions++;
                        long bytes = (long)length;
                        bool writable = (region.Protect & (PageReadWrite | PageWriteCopy
                            | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;
                        bool executable = (region.Protect & (PageExecute | PageExecuteRead
                            | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;
                        bool guard = (region.Protect & PageGuard) != 0;

                        if (region.Type == MemImage)
                        {
                            // A writable page in a library is its data section, and it is
                            // copy-on-write: ours the moment anything touches it. The rest
                            // is the same physical memory in every process that loaded it.
                            if (writable)
                            {
                                imagePrivate += bytes;
                                string name = moduleAt.TryGetValue(region.AllocationBase.ToInt64(), out string? module)
                                    ? module : "unnamed image";
                                perModule[name] = perModule.GetValueOrDefault(name) + bytes;
                            }
                            else imageShared += bytes;
                        }
                        else if (region.Type == MemMapped)
                        {
                            mapped += bytes;
                        }
                        else
                        {
                            if (guard) privateOther += bytes;
                            else if (executable) privateExecutable += bytes;
                            else if (writable) privateWritable += bytes;
                            else privateOther += bytes;

                            // Accumulated per reservation rather than per region, because
                            // one allocation is reported as many regions once parts of it
                            // have had their protection changed.
                            if (!guard)
                            {
                                ulong at = (ulong)region.AllocationBase.ToInt64();
                                currentBlock[at] = currentBlock.GetValueOrDefault(at) + bytes;
                            }
                        }
                    }

                    ulong next = address + length;
                    if (next <= address) break;
                    address = next;
                }

                foreach (KeyValuePair<ulong, long> block in currentBlock)
                    blocks.Add((block.Value, block.Key, "private"));

                long privateTotal = privateWritable + privateExecutable + privateOther;

                Logger.WriteLine($"    committed: private {Mb(privateTotal)}, "
                    + $"image {Mb(imagePrivate)} ours + {Mb(imageShared)} shared, "
                    + $"mapped {Mb(mapped)}, in {regions} regions "
                    + $"(reserved, uncommitted, not a cost: {Mb(reserved)})");

                Logger.WriteLine($"    private breakdown: writable {Mb(privateWritable)} "
                    + $"(heaps, stacks, driver memory), executable {Mb(privateExecutable)} "
                    + $"(compiled code), other {Mb(privateOther)}");

                // Taken after the walk, so a heap that grew during it is not counted in
                // one place and missed in the other.
                List<(IntPtr Handle, long Committed, int Regions)> heaps = WalkHeaps();
                var heapAt = new Dictionary<ulong, int>();
                for (int i = 0; i < heaps.Count; i++)
                    heapAt[(ulong)heaps[i].Handle.ToInt64()] = i;

                long heapTotal = heaps.Sum(h => h.Committed);
                Logger.WriteLine($"    native heaps: {heaps.Count} holding {Mb(heapTotal)} "
                    + $"({Mb(privateTotal - heapTotal)} of private is not a heap - "
                    + "runtime, stacks, and the graphics driver's own allocations)");

                foreach ((IntPtr handle, long committed, int regionCount) in
                    heaps.OrderByDescending(h => h.Committed).Take(8))
                    Logger.WriteLine($"        {Mb(committed),6}  heap at 0x{handle.ToInt64():X} "
                        + $"in {regionCount} segment(s)");

                Logger.WriteLine("    largest private allocations:");
                foreach ((long bytes, ulong at, _) in blocks.OrderByDescending(b => b.Size).Take(12))
                {
                    string owner = heapAt.TryGetValue(at, out int which)
                        ? $"  <- native heap #{which}" : "";
                    Logger.WriteLine($"        {Mb(bytes),6}  at 0x{at:X}{owner}");
                }

                Logger.WriteLine("    library data sections charged to us:");
                foreach (KeyValuePair<string, long> module in perModule.OrderByDescending(m => m.Value).Take(10))
                    Logger.WriteLine($"        {Mb(module.Value),6}  {module.Key}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Memory regions failed: " + ex.Message);
            }
        }

        private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1}MB";
    }
}
