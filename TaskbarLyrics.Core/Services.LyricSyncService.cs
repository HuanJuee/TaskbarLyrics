using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.Core.Services;

public sealed class LyricSyncService : IDisposable
{
    private readonly ILyricProviderRegistry _registry;
    private readonly Func<string?, bool> _shouldShowTranslation;
    private TrackInfo? _currentTrack;
    private string? _currentTrackId;
    private LyricDocument? _currentDocument;
    private string? _currentLyricSourceApp;
    private int _lastAcceptedLineIndex = -1;
    private TimeSpan? _lastAcceptedPosition;
    private bool _isUpdating;
    private CancellationTokenSource? _searchCts;
    private bool _isDisposed;
    private int _lastEmittedLineIndex = -1;
    private long _documentLoadedTicks;

    public string? CurrentLyricSourceApp => _currentLyricSourceApp;

    public LyricSyncService(ILyricProviderRegistry registry, Func<string?, bool>? shouldShowTranslation = null)
    {
        _registry = registry;
        _shouldShowTranslation = shouldShowTranslation ?? (_ => true);
    }

    public Task<LyricDisplayFrame> GetDisplayFrameAsync(PlaybackSnapshot snapshot)
    {
        if (snapshot.Track == null)
        {
            CancelPendingSearch();
            _currentTrack = null;
            _currentTrackId = null;
            _currentDocument = null;
            _currentLyricSourceApp = null;
            ResetLineStabilizer();
            _lastEmittedLineIndex = -1;
            return Task.FromResult(new LyricDisplayFrame("", "", "", 0, -1));
        }

        var trackId = BuildTrackIdentity(snapshot.Track);
        if (trackId != _currentTrackId)
        {
            _currentTrack = snapshot.Track;
            _currentTrackId = trackId;
            _currentDocument = null;
            _currentLyricSourceApp = null;
            ResetLineStabilizer();
            _lastEmittedLineIndex = -1;
            _ = UpdateLyricsAsync(snapshot.Track, trackId);
        }

        if (_currentDocument == null || _currentDocument.Lines.Count == 0)
        {
            return Task.FromResult(new LyricDisplayFrame(
                _isUpdating ? "正在匹配歌词..." : "暂未找到歌词",
                "",
                _currentTrack?.Title ?? "",
                0, -1));
        }

        // Apply player-specific compensation
        var sourceLead = LyricMatchingPolicy.GetPlayerLeadTime(_currentTrack?.SourceApp);
        var position = snapshot.Position + sourceLead;

        var lines = _currentDocument.Lines;
        var currentIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= position) currentIdx = i;
            else break;
        }

        // Grace period: for the first 300ms after lyrics load, SMTC position
        // is often stale or over-extrapolated (residual from the previous track).
        // Force lineIndex to 0 to avoid showing the wrong starting line.
        var msSinceLoad = Environment.TickCount64 - _documentLoadedTicks;
        if (msSinceLoad < 300 && _lastEmittedLineIndex < 0)
        {
            currentIdx = currentIdx < 0 ? -1 : 0;
        }

        // Monotonic guard: prevent backward index jumps caused by
        // SMTC timeline extrapolation oscillation within the same track.
        if (currentIdx >= 0 && currentIdx < _lastEmittedLineIndex)
        {
            currentIdx = _lastEmittedLineIndex;
        }
        if (currentIdx >= 0)
        {
            _lastEmittedLineIndex = currentIdx;
        }

        var displayIdx = StabilizeLineIndex(position, currentIdx < 0 ? 0 : currentIdx);

        if (displayIdx == 0 && currentIdx == -1)
        {
            // If before first line, show the first line as prepared current
            var firstLine = lines[0];
            string firstText = firstLine.Text;
            if (CanShowTranslation() && !string.IsNullOrWhiteSpace(firstLine.Translation))
            {
                firstText += " (" + firstLine.Translation + ")";
            }

            var nextTxt = lines.Count > 1 ? lines[1].Text : "";
            if (CanShowTranslation() && lines.Count > 1 && !string.IsNullOrWhiteSpace(lines[1].Translation))
            {
                nextTxt += " (" + lines[1].Translation + ")";
            }

            return Task.FromResult(new LyricDisplayFrame(firstText, nextTxt, _currentTrack?.Title ?? "", 0, 0));
        }

        var currentLine = lines[displayIdx];
        var nextLine = (displayIdx + 1 < lines.Count) ? lines[displayIdx + 1] : null;

        // Smart text merging: if translation exists, append it.
        // This ensures the "NextLine" correctly shows the next lyric for animation,
        // while still making translations visible in the taskbar's limited space.
        string currentText = currentLine.Text;
        if (CanShowTranslation() && !string.IsNullOrWhiteSpace(currentLine.Translation))
        {
            // We use a small space and parens for a clean look in the taskbar
            currentText += " (" + currentLine.Translation + ")";
        }

        string nextText = nextLine?.Text ?? "";
        if (CanShowTranslation() && nextLine != null && !string.IsNullOrWhiteSpace(nextLine.Translation))
        {
            nextText += " (" + nextLine.Translation + ")";
        }

        // Calculate progress within line for syllable animation
        double progress = 0;
        if (nextLine != null)
        {
            var duration = nextLine.Timestamp - currentLine.Timestamp;
            var elapsed = position - currentLine.Timestamp;
            if (duration > TimeSpan.Zero)
            {
                progress = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            }
        }

        return Task.FromResult(new LyricDisplayFrame(
            currentText,
            nextText,
            _currentTrack?.Title ?? "",
            progress,
            displayIdx
        ));
    }

    private async Task UpdateLyricsAsync(TrackInfo track, string trackId)
    {
        // Cancel any ongoing search for the previous track immediately
        CancelPendingSearch();
        _searchCts = new CancellationTokenSource();
        var cts = _searchCts;

        _isUpdating = true;

        try
        {
            var results = await _registry.ResolveLyricsAsync(track, cts.Token);

            if (cts.IsCancellationRequested) return;
            // Pick the best match
            var bestResult = results
                .Where(r => r.Document != null && r.Document.Lines.Count > 0)
                .OrderByDescending(r => r.Document!.BestScore)
                .ThenBy(r => r.SourceApp == "QQMusic" || r.SourceApp == "Netease" ? 0 : 1) 
                .FirstOrDefault();

            if (bestResult != null && _currentTrackId == trackId)
            {
                _currentDocument = bestResult.Document;
                _currentLyricSourceApp = bestResult.SourceApp;
                _documentLoadedTicks = Environment.TickCount64;
                ResetLineStabilizer();
                _lastEmittedLineIndex = -1;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer track replaced this request.
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                _isUpdating = false;
            }

            cts.Dispose();
        }
    }

    private static string BuildTrackIdentity(TrackInfo track)
    {
        // SMTC metadata can arrive in waves: SongId and Duration are often filled
        // or corrected after lyrics have already loaded. They should not reset the
        // lyric document for the same visible song.
        return $"{NormalizeIdentityPart(track.SourceApp)}|{NormalizeIdentityPart(track.Title)}|{NormalizeIdentityPart(track.Artist)}";
    }

    private static string NormalizeIdentityPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private bool CanShowTranslation()
    {
        return _shouldShowTranslation(_currentLyricSourceApp);
    }

    private int StabilizeLineIndex(TimeSpan position, int candidateIndex)
    {
        const int jitterBackToleranceMs = 1200;

        var acceptedIndex = candidateIndex;
        if (_lastAcceptedLineIndex >= 0 &&
            candidateIndex < _lastAcceptedLineIndex &&
            _lastAcceptedPosition.HasValue &&
            position >= _lastAcceptedPosition.Value - TimeSpan.FromMilliseconds(jitterBackToleranceMs))
        {
            acceptedIndex = _lastAcceptedLineIndex;
        }

        _lastAcceptedLineIndex = acceptedIndex;
        if (!_lastAcceptedPosition.HasValue || position > _lastAcceptedPosition.Value)
        {
            _lastAcceptedPosition = position;
        }
        else if (acceptedIndex == candidateIndex)
        {
            _lastAcceptedPosition = position;
        }

        return acceptedIndex;
    }

    private void ResetLineStabilizer()
    {
        _lastAcceptedLineIndex = -1;
        _lastAcceptedPosition = null;
    }

    private void CancelPendingSearch()
    {
        var cts = _searchCts;
        _searchCts = null;
        _isUpdating = false;
        cts?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelPendingSearch();
    }

}
