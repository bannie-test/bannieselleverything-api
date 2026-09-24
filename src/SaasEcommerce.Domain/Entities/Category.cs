using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

public class Category : TenantEntityBase
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Guid? ParentId { get; set; }
    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = [];
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
