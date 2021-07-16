namespace MigrationTools.DataContracts
{
    public class LinkItem
    {
        public LinkItemType LinkType { get; set; }
        public string LinkUri { get; set; }
        public string Comment { get; set; }
        public string ArtifactLinkType { get; set; }
        public int RelatedWorkItem { get; set; }
        public string LinkTypeEndImmutableName { get; set; }
        public string LinkTypeEndName { get; set; }
    }

    public enum LinkItemType
    {
        Hyperlink,
        ExternalLink,
        RelatedLink
    }
}