using SaasEcommerce.Domain.Common;

namespace SaasEcommerce.Domain.Entities;

/// <summary>One review per customer per product. Product.RatingAverage/ReviewCount are recalculated on every change.</summary>
public class ProductReview : TenantEntityBase
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>1 to 5 stars.</summary>
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }

    /// <summary>Display name at the time of writing, so reviews don't expose the customer's email.</summary>
    public string AuthorName { get; set; } = "";

    /// <summary>True when the customer had a delivered order containing this product when they wrote the review.</summary>
    public bool IsVerifiedPurchase { get; set; }
}
