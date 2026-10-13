using PdfModifier.Core.Pdfium;
using PdfSharp.Pdf;

namespace PdfModifier.Core.Operations;

public sealed record PdfMetadata
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Producer { get; init; } = "";
    public string CreationDate { get; init; } = "";
    public string ModificationDate { get; init; } = "";
}

public static class PdfMetadataOps
{
    public static PdfMetadata Read(byte[] pdf)
    {
        lock (PdfDoc.Sync)
        {
            using var doc = PdfDoc.Load(pdf);
            return new PdfMetadata
            {
                Title = doc.GetMetaText("Title"),
                Author = doc.GetMetaText("Author"),
                Subject = doc.GetMetaText("Subject"),
                Keywords = doc.GetMetaText("Keywords"),
                Creator = doc.GetMetaText("Creator"),
                Producer = doc.GetMetaText("Producer"),
                CreationDate = doc.GetMetaText("CreationDate"),
                ModificationDate = doc.GetMetaText("ModDate")
            };
        }
    }

    /// <summary>
    /// Sets title, author, subject, keywords and creator. The XMP packet is dropped because it would otherwise keep the
    /// old values. Producer and dates are maintained by the PDF writer.
    /// </summary>
    public static byte[] Write(byte[] pdf, PdfMetadata metadata) => Sharp.Rewrite(pdf, doc =>
    {
        doc.Info.Title = metadata.Title;
        doc.Info.Author = metadata.Author;
        doc.Info.Subject = metadata.Subject;
        doc.Info.Keywords = metadata.Keywords;
        doc.Info.Creator = metadata.Creator;
        doc.Internals.Catalog.Elements.Remove("/Metadata");
    });

    /// <summary>Removes the document information and the XMP metadata, including custom entries.</summary>
    public static byte[] RemoveAll(byte[] pdf)
    {
        var rewritten = Sharp.Rewrite(pdf, doc =>
        {
            foreach (var key in doc.Info.Elements.Keys.ToList()) doc.Info.Elements.Remove(key);
            doc.Internals.Catalog.Elements.Remove("/Metadata");
            doc.Internals.Catalog.Elements.Remove("/PieceInfo");
            foreach (PdfSharp.Pdf.PdfPage page in doc.Pages)
            {
                page.Elements.Remove("/Metadata");
                page.Elements.Remove("/PieceInfo");
            }
        });
        // The writer stamps its own producer and dates into a fresh info dictionary; unhook it.
        return Sharp.DetachInfoDictionary(rewritten);
    }
}

public static class PdfSecurity
{
    /// <summary>Encrypts the document (AES-256). The user password is required to open it.</summary>
    public static byte[] Protect(byte[] pdf, string userPassword, string? ownerPassword = null)
    {
        if (string.IsNullOrEmpty(userPassword)) throw new ArgumentException("A password is required.", nameof(userPassword));
        return Sharp.Rewrite(pdf, doc =>
        {
            doc.SecurityHandler.SetEncryptionToV5();
            doc.SecuritySettings.UserPassword = userPassword;
            doc.SecuritySettings.OwnerPassword = string.IsNullOrEmpty(ownerPassword) ? userPassword : ownerPassword;
        });
    }
}
