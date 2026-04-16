namespace AgenticOrchestra.Services;

/// <summary>
/// Allows the orchestrator to physically interact with the filesystem.
/// </summary>
public sealed class NativeFileService
{
    public string ReadFile(string path)
    {
        try 
        {
            if (!File.Exists(path)) return $"(Error: File not found at {path})";
            string content = File.ReadAllText(path);
            return content;
        } 
        catch (Exception ex) 
        {
            return $"(Error: {ex.Message})";
        }
    }

    public string WriteFile(string path, string content)
    {
        try 
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, content);
            return $"(Success: File {path} written successfully.)";
        } 
        catch (Exception ex) 
        {
            return $"(Error: {ex.Message})";
        }
    }
}
