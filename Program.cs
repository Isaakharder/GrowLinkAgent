using FlowMasterAgent.Forms;
using FlowMasterAgent.Services;

namespace FlowMasterAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            MessageBox.Show($"Unexpected error: {e.Exception.Message}", "GrowLinkAgent",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        var log = new LogService();
        var config = new ConfigService(log);
        var credentials = new CredentialService(log);
        var upload = new UploadService(config, credentials, log);
        var watcher = new FolderWatcherService(config, upload, log);

        Application.Run(new MainForm(config, credentials, upload, watcher, log));
    }
}
