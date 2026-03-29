using System.Security.Cryptography;
using System.Text;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

internal static class WorkerPreviewFingerprint
{
    public static string Compute(WorkerPreviewResult preview)
    {
        var builder = new StringBuilder();
        builder.Append(preview.WorkerId).Append('|');
        builder.Append(preview.SamAccountName).Append('|');
        builder.Append(preview.TargetOu).Append('|');
        builder.Append(preview.ManagerDistinguishedName).Append('|');
        builder.Append(preview.CurrentEnabled?.ToString() ?? "null").Append('|');
        builder.Append(preview.ProposedEnable?.ToString() ?? "null").Append('|');

        foreach (var bucket in preview.Buckets)
        {
            builder.Append(bucket).Append('|');
        }

        foreach (var row in preview.DiffRows)
        {
            builder.Append(row.Attribute).Append('=')
                .Append(row.Before).Append("->")
                .Append(row.After).Append(';');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }
}
