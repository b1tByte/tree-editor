namespace TreeEditor.Api.Data;

/// <summary>A node of the initial sample tree.</summary>
public sealed record SeedNode(string Value, params SeedNode[] Children);

/// <summary>
/// Initial sample data: a single root with several branches and up to six levels of nesting,
/// so lazy loading, out-of-order caching and cascading deletes can all be exercised.
/// </summary>
public static class SeedData
{
    public static readonly SeedNode[] Roots =
    [
        new("Catalog",
            new SeedNode("Electronics",
                new SeedNode("Computers",
                    new SeedNode("Laptops",
                        new SeedNode("Business",
                            new SeedNode("ThinkPad X1"),
                            new SeedNode("Latitude 7450")),
                        new SeedNode("Gaming",
                            new SeedNode("ROG Zephyrus"),
                            new SeedNode("Legion Pro"))),
                    new SeedNode("Desktops",
                        new SeedNode("Workstations"),
                        new SeedNode("Mini PCs"))),
                new SeedNode("Phones",
                    new SeedNode("Android",
                        new SeedNode("Pixel 9"),
                        new SeedNode("Galaxy S25")),
                    new SeedNode("iOS",
                        new SeedNode("iPhone 16")))),
            new SeedNode("Books",
                new SeedNode("Fiction",
                    new SeedNode("Science Fiction",
                        new SeedNode("Dune"),
                        new SeedNode("Foundation")),
                    new SeedNode("Mystery")),
                new SeedNode("Non-fiction",
                    new SeedNode("History"),
                    new SeedNode("Science",
                        new SeedNode("Physics"),
                        new SeedNode("Biology")))),
            new SeedNode("Home & Garden",
                new SeedNode("Furniture",
                    new SeedNode("Chairs"),
                    new SeedNode("Tables")),
                new SeedNode("Tools")))
    ];
}
