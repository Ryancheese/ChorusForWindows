using System.ComponentModel;
using System.Runtime.CompilerServices;
using Chorus.Host.Localization;
using ChorusCore.Network.Mdns;

namespace Chorus.Host.ViewModels;

/// <summary>Discovered peer row with connect/disconnect label for the nearby list.</summary>
public sealed class PeerRowViewModel : INotifyPropertyChanged
{
    private bool _isConnected;

    public PeerRowViewModel(DiscoveredPeer peer) => Peer = peer;

    public DiscoveredPeer Peer { get; }
    public string InstanceName => Peer.InstanceName;
    public string IPDisplay => Peer.IPAddress.ToString();
    public string EndpointKey => $"{Peer.IPAddress}:{Peer.Port}";

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionLabel)));
        }
    }

    public string ActionLabel => IsConnected ? L10n.T("action.disconnect") : L10n.T("action.connect");

    public void RefreshLabels() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionLabel)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
