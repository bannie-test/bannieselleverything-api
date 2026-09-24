using System.Globalization;
using System.Text;

namespace SaasEcommerce.Api.Common;

public static class Slug
{
    /// <summary>"Áo thun Đỏ" → "ao-thun-do". Strips diacritics (including Vietnamese đ) and collapses separators.</summary>
    public static string From(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var pendingDash = false;

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && sb.Length > 0)
                    sb.Append('-');
                sb.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return sb.ToString();
    }
}
