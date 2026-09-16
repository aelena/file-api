using System.CommandLine;
using Aelena.FileApi.Cli.Commands;

var root = new RootCommand("fileapi — Document processing CLI powered by Aelena.FileApi.Core")
{
#if INCLUDE_PDF
    PdfCommand.Create(),
#endif
    DocxCommand.Create(),
    ImageCommand.Create(),
    HashCommand.Create(),
    TxtCommand.Create(),
    ZipCommand.Create(),
    ReadabilityCommand.Create(),
    PiiCommand.Create(),
    EmailCommand.Create(),
    ConvertCommand.Create()
};

// Every command maps its own failures onto an exit code (see CommandExtensions.
// WithAction), so the framework's catch-all — which prints a raw stack trace — is
// only reached by a genuine defect, and is left on deliberately for that case.
return root.Parse(args).Invoke();
