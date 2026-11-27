using System.Text.Json;
using CloudOrc.WorkspaceUI.Services;
using CloudOrc.WorkspaceUI.Ui;

namespace CloudOrc.WorkspaceUI.Tests;

/// <summary>Backup tab wording and numbers (same words as the customer portal).</summary>
public class BackupTextTests
{
    [Theory]
    [InlineData("SUCCEEDED", "Done", "ok")]
    [InlineData("PARTIAL", "Done with warnings", "warn")]
    [InlineData("FAILED", "Failed", "bad")]
    [InlineData("CANCELLED", "Cancelled", "muted")]
    [InlineData("TRANSFERRING", "Uploading", "run")]
    [InlineData("QUEUED", "Preparing", "run")]
    public void Job_states_read_like_the_portal(string state, string label, string tone)
    {
        Assert.Equal(label, BackupText.JobState(state));
        Assert.Equal(tone, BackupText.JobTone(state));
    }

    [Fact]
    public void Reasons_are_customer_friendly_and_hide_engine_internals()
    {
        Assert.StartsWith("Your data is larger than the backup storage", BackupText.Reason("BACKUP_QUOTA_EXCEEDED", null));
        Assert.Equal("The backup did not complete. Please try again or contact support.", BackupText.Reason("SOMETHING_NEW", "kopia: repository lock"));
        Assert.Equal("Disk was full", BackupText.Reason(null, "Disk was full"));
        Assert.Null(BackupText.Reason(null, null));
    }

    [Fact]
    public void Restore_status_matches_the_portal()
    {
        Assert.Equal(("Waiting for approval", "warn"), BackupText.RestoreStatus(new RestoreRow { State = "QUEUED", ApprovalState = "pending" }));
        Assert.Equal(("Not approved", "muted"), BackupText.RestoreStatus(new RestoreRow { State = "CANCELLED", ApprovalState = "rejected" }));
        Assert.Equal(("Completed", "ok"), BackupText.RestoreStatus(new RestoreRow { State = "SUCCEEDED" }));
        Assert.Equal(("In progress", "run"), BackupText.RestoreStatus(new RestoreRow { State = "TRANSFERRING" }));
    }

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(812d, "812 B")]
    [InlineData(48213d, "47.1 KB")]
    [InlineData(38168166d, "36.4 MB")]
    public void Sizes_are_readable(double bytes, string text) => Assert.Equal(text, BackupText.Bytes(bytes));

    [Fact]
    public void Folder_labels_and_schedules_are_readable()
    {
        Assert.Equal("CloudDrive", BackupText.FolderLabel(@"D:\2031003506\SAYJAAN-3506\CloudDrive\"));
        Assert.Equal("Every day at 22:00", BackupText.Schedule(JsonDocument.Parse("""{"kind":"daily","timesOfDay":["22:00"]}""").RootElement));
        Assert.Equal("Every 6 hours", BackupText.Schedule(JsonDocument.Parse("""{"kind":"interval","intervalHours":6}""").RootElement));
        Assert.Equal("Only when you start it", BackupText.Schedule(JsonDocument.Parse("""{"kind":"manual"}""").RootElement));
        Assert.Equal("Not scheduled", BackupText.Schedule(default));
    }
}
