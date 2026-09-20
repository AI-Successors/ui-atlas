using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UiAtlas.Core.Recording.Windows;

public sealed record AzureHeaderSettings(bool Enabled = false, string Endpoint = "", string Deployment = "gpt-5.6-sol")
{
    public AzureHeaderSettings Normalize()
    {
        if (!Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.TrimEnd('/') is not ("" or "/openai/v1" or "/openai/v1/responses"))
            throw new AzureHeaderException("Enter an HTTPS Azure resource endpoint, optionally ending in /openai/v1/.");
        if (string.IsNullOrWhiteSpace(Deployment) || Deployment.Length > 128 || Deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new AzureHeaderException("Enter the Azure deployment name for GPT-5.6 Sol.");
        return this with { Endpoint = endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/'), Deployment = Deployment.Trim() };
    }
}

public interface IAzureHeaderCredentialStore
{
    string? Read(string target);
    void Write(string target, string key);
}

public sealed class AzureHeaderSettingsStore
{
    private readonly string _path;
    private readonly IAzureHeaderCredentialStore _credentials;
    public AzureHeaderSettingsStore(string? path = null, IAzureHeaderCredentialStore? credentials = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "azure-header-settings.json");
        _credentials = credentials ?? new WindowsHeaderCredentials();
    }
    public AzureHeaderSettings Load() => !File.Exists(_path) ? new() :
        JsonSerializer.Deserialize<AzureHeaderSettings>(File.ReadAllText(_path)) ?? throw new AzureHeaderException("Recorder header settings could not be read. Save them again.");
    public bool HasKey(AzureHeaderSettings settings) => !string.IsNullOrWhiteSpace(_credentials.Read(Target(settings.Normalize())));
    public AzureOpenAiHeaderReader CreateReader(AzureHeaderSettings settings, string? newKey = null) =>
        new(settings, string.IsNullOrEmpty(newKey) ? _credentials.Read(Target(settings.Normalize())) ?? "" : newKey);
    public AzureOpenAiTableReader CreateTableReader()
    {
        var settings = Load();
        if (!settings.Enabled) throw new AzureHeaderException("Enable the saved Azure provider in Recorder settings before reading table data.");
        return new(settings, _credentials.Read(Target(settings.Normalize())) ?? "");
    }
    public void Save(AzureHeaderSettings settings, string? newKey = null)
    {
        if (settings.Enabled) settings = settings.Normalize();
        // Disabling cloud reading must remain possible even if its configuration is invalid.
        if (settings.Enabled && !string.IsNullOrEmpty(newKey)) _credentials.Write(Target(settings), newKey);
        if (settings.Enabled && !HasKey(settings)) throw new AzureHeaderException("Enter an Azure API key before enabling Azure header reading.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Target(AzureHeaderSettings settings) => "UiAtlas.AzureHeaders:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Endpoint)));

    private sealed class WindowsHeaderCredentials : IAzureHeaderCredentialStore
    {
        public string? Read(string target)
        {
            if (!CredRead(target, 1, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new AzureHeaderException("Windows could not read the saved Azure key.");
            }
            try { var credential = Marshal.PtrToStructure<Credential>(pointer);
                return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)); }
            finally { CredFree(pointer); }
        }
        public void Write(string target, string key)
        {
            if (key.Length > 2500) throw new AzureHeaderException("The API key is too long.");
            var pointer = Marshal.StringToCoTaskMemUni(key);
            try
            {
                var credential = new Credential { Type = 1, Target = target, BlobSize = (uint)(key.Length * 2), Blob = pointer, Persist = 2, UserName = "Azure OpenAI" };
                if (!CredWrite(ref credential, 0)) throw new AzureHeaderException("Windows could not save the Azure key.");
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Credential
        {
            public uint Flags, Type; public string Target; public string? Comment; public long LastWritten;
            public uint BlobSize; public nint Blob; public uint Persist, AttributeCount; public nint Attributes;
            public string? Alias; public string UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
    }
}
