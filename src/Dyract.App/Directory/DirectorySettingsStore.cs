using Microsoft.Maui.Storage;

namespace Dyract.App.Directory;

public interface IDirectorySettingsStore
{
    Uri? GetBaseUri();
    Uri SetBaseUri(string value);
    void Clear();
}

public sealed class DirectorySettingsStore : IDirectorySettingsStore
{
    private const string BaseUriKey = "dyract.directory.base-uri.v1";

    public Uri? GetBaseUri()
    {
        var value = Preferences.Default.Get(BaseUriKey, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TryNormalize(value, out var uri) ? uri : null;
    }

    public Uri SetBaseUri(string value)
    {
        if (!TryNormalize(value, out var uri))
        {
            throw new ArgumentException(
                "Directory URL must be an absolute HTTPS URL without credentials, query, or fragment.",
                nameof(value));
        }

        Preferences.Default.Set(BaseUriKey, uri.AbsoluteUri);
        return uri;
    }

    public void Clear() => Preferences.Default.Remove(BaseUriKey);

    private static bool TryNormalize(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            Path = string.IsNullOrWhiteSpace(parsed.AbsolutePath) ? "/" : parsed.AbsolutePath.TrimEnd('/') + "/"
        };

        uri = builder.Uri;
        return true;
    }
}
