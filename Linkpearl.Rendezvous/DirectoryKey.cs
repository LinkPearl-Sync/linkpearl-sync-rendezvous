using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La clé qui signe la liste du cercle ouvert.
/// </summary>
/// <remarks>
/// Sa partie publique est inscrite dans le plugin : la perdre oblige à publier
/// une nouvelle version pour que les clients acceptent une autre clé. D'où la
/// règle, la même que pour le sel de bans.json : un fichier présent mais
/// illisible arrête le démarrage, il n'est jamais remplacé.
/// </remarks>
public static class DirectoryKey
{
    public static ECDsa LoadOrCreate(string path, TextWriter? log = null)
    {
        log ??= Console.Out;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(path))
        {
            try
            {
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                return key;
            }
            catch (CryptographicException e)
            {
                key.Dispose();
                throw new InvalidOperationException(
                    $"{path} illisible. Ne pas le régénérer : le restaurer depuis une sauvegarde.", e);
            }
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        // « ! » et non « is false » : c'est la forme que l'analyseur de plateforme reconnaît comme une garde.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, options))
            stream.Write(key.ExportPkcs8PrivateKey());

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // La clé publique n'a rien de secret, et c'est elle qu'il faut recopier
        // dans le plugin : la donner ici évite d'avoir à la recalculer.
        log.WriteLine(
            $"Clé d'autorité engendrée dans {path}. Clé publique à inscrire dans le plugin : "
            + Convert.ToHexStringLower(ServiceConsensus.PublicPoint(key)));

        return key;
    }
}
