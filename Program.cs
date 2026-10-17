using FlowMasterAgent.Forms;
using FlowMasterAgent.Services;

namespace FlowMasterAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // One agent per computer: a second launch shows the running one and exits.
        using var instance = SingleInstance.Acquire();
        if (instance.State == SingleInstance.Result.RunningForThisUser)
            return;
        if (instance.State == SingleInstance.Result.RunningForAnotherUser)
        {
            MessageBox.Show("GrowLinkAgent is already running on this computer for another Windows user. Only one agent may run at a time.",
                "GrowLinkAgent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

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

        var form = new MainForm(config, credentials, upload, watcher, log);
        instance.OnShowRequested(form.ShowFromAnotherLaunch);
        Application.Run(form);
    }
}
