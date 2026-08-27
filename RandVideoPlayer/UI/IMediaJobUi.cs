using System;

namespace RandVideoPlayer.UI;

/// <summary>
/// What MainForm's "run ffmpeg, then swap the result over the original" machinery
/// needs from the window that asked for the job. Implemented by both
/// <see cref="CutWindow"/> and <see cref="AudioWindow"/> so the verify /
/// backup / Recycle-Bin flow is written once.
/// </summary>
public interface IMediaJobUi
{
    bool IsDisposed { get; }

    /// <summary>Marshals onto the window's thread and drops the call if it has gone away.</summary>
    void PostToUi(Action action);

    void SetBusy(bool busy, string status);
    void ReportProgress(double frac);
    void ReportDone(bool success, string message);
}
