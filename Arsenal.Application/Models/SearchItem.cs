namespace Arsenal.Application.Models
{
    public class SearchItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string PageTag { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "\uE713"; // Settings glyph default
        public Action? Action { get; set; }

        /// <summary>
        /// Extra terms people actually type, which the title does not contain: "od" for
        /// overdrive, "hz" for refresh rate, "mux" for Ultimate, and so on.
        /// </summary>
        public string[] Keywords { get; set; } = Array.Empty<string>();

        /// <summary>Score from the last search, used to order results.</summary>
        public int Relevance { get; set; }
    }
}
