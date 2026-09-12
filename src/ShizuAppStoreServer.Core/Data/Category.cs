namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One <c>(section, ### category, #### subcategory?)</c> combination from the
/// awesome list. Subcategories (Vendor-specific → Pixel/OneUI/…) are modelled
/// as child rows via <see cref="ParentId"/>.
/// </summary>
public sealed class Category
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Globally unique URL slug.</summary>
    public required string Slug { get; set; }

    public CategorySection Section { get; set; }

    public long? ParentId { get; set; }
    public Category? Parent { get; set; }
    public List<Category> Children { get; } = [];

    public List<App> Apps { get; } = [];
}
