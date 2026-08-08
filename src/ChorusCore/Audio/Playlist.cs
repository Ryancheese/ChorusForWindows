using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace ChorusCore.Audio;

/// <summary>One row in the Host's local music playlist.</summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string FilePath { get; init; } = "";

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Host-side local music playlist. Holds a flat list of audio file paths plus the
/// currently-playing index and auto-advance flag. View-models subscribe to <see cref="Changed"/>
/// to refresh UI.
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    public ObservableCollection<PlaylistItem> Items { get; } = new();
    public event Action? Changed;

    private int _currentIndex = -1;
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (_currentIndex == value) return;
            // 更新所有 item 的 IsCurrent（用于 UI 高亮）
            for (int i = 0; i < Items.Count; i++)
                Items[i].IsCurrent = (i == value);
            _currentIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Current));
            Changed?.Invoke();
        }
    }

    private bool _autoAdvance = true;
    public bool AutoAdvance
    {
        get => _autoAdvance;
        set { if (_autoAdvance != value) { _autoAdvance = value; OnPropertyChanged(); Changed?.Invoke(); } }
    }

    public PlaylistItem? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public void AddFile(string filePath)
    {
        var title = Path.GetFileNameWithoutExtension(filePath);
        Items.Add(new PlaylistItem { Title = title, FilePath = filePath });
        if (CurrentIndex < 0) CurrentIndex = 0;
        Changed?.Invoke();
    }

    public void AddFolder(string folderPath)
    {
        try
        {
            var files = Directory.EnumerateFiles(folderPath)
                .Where(f => AudioFormats.IsSupported(f))
                .OrderBy(f => Path.GetFileName(f));
            foreach (var f in files) AddFile(f);
        }
        catch { /* bad folder */ }
    }

    public void Remove(Guid id)
    {
        var idx = -1;
        for (int i = 0; i < Items.Count; i++) if (Items[i].Id == id) { idx = i; break; }
        if (idx < 0) return;
        Items.RemoveAt(idx);
        if (CurrentIndex >= Items.Count) CurrentIndex = Items.Count - 1;
        OnPropertyChanged(nameof(Current));
        Changed?.Invoke();
    }

    public void Select(Guid id)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Id == id) { CurrentIndex = i; return; }
    }

    public PlaylistItem? MoveToNext()
    {
        if (Items.Count == 0) return null;
        if (CurrentIndex + 1 < Items.Count) { CurrentIndex++; return Current; }
        return null;
    }

    public PlaylistItem? MoveToPrevious()
    {
        if (Items.Count == 0) return null;
        if (CurrentIndex > 0) { CurrentIndex--; return Current; }
        return null;
    }

    public void Clear()
    {
        Items.Clear();
        CurrentIndex = -1;
        OnPropertyChanged(nameof(Current));
        Changed?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
