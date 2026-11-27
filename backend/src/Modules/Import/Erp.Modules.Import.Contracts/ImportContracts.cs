namespace Erp.Modules.Import.Contracts;

public static class ImportPermissions
{
    public const string Read = "import.jobs.read";

    /// <summary>Upload, validate and commit imports. Committing also requires the target module's own permission.</summary>
    public const string Write = "import.jobs.write";
}
