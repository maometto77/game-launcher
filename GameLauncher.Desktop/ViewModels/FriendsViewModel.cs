using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Desktop.Services.Friends;
using GameLauncher.Desktop.Services.Settings;
using GameLauncher.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.ViewModels;

/// <summary>
/// View model for the Friends page.
/// </summary>
/// <remarks>
/// Never blocks on the network. The list renders from cache immediately, the
/// connection banner reflects whatever state the supervisor has reached, and
/// actions that need a connection fail with a message rather than hanging.
/// </remarks>
public sealed partial class FriendsViewModel : ViewModelBase, IDisposable
{
    private readonly IFriendsService _friends;
    private readonly ISettingsService _settings;
    private readonly ILogger<FriendsViewModel> _logger;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<FriendListEntry> _entries = [];

    [ObservableProperty]
    private string _friendCodeInput = string.Empty;

    [ObservableProperty]
    private string _myFriendCode = string.Empty;

    [ObservableProperty]
    private RelayConnectionState _connectionState = RelayConnectionState.Disabled;

    [ObservableProperty]
    private string _connectionText = string.Empty;

    [ObservableProperty]
    private bool _isUsingCachedData;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="friends">Friends list and relay actions.</param>
    /// <param name="settings">Supplies this installation's friend code.</param>
    /// <param name="logger">Logger for page diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public FriendsViewModel(
        IFriendsService friends,
        ISettingsService settings,
        ILogger<FriendsViewModel> logger)
    {
        _friends = friends ?? throw new ArgumentNullException(nameof(friends));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _friends.FriendsChanged += OnFriendsChanged;
        _friends.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <summary>Gets a value indicating whether a friend request can be sent.</summary>
    public bool CanSendRequest =>
        ConnectionState == RelayConnectionState.Connected &&
        FriendCodeContract.IsValid(FriendCodeContract.Normalize(FriendCodeInput));

    /// <summary>Gets a value indicating whether no relay is configured at all.</summary>
    public bool IsRelayUnconfigured => ConnectionState == RelayConnectionState.Disabled;

    /// <inheritdoc />
    public override async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        MyFriendCode = _settings.Current.EffectiveFriendCode;
        ApplyConnectionState(_friends.ConnectionState, null);
        RefreshEntries();

        // Attempted, not awaited for correctness: the cached list is already on
        // screen, and a failure here simply leaves it there.
        await _friends.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Re-fetches the friend list from the relay.</summary>
    /// <returns>A task that completes when the refresh has finished.</returns>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        ClearError();
        IsBusy = true;

        try
        {
            StatusText = await _friends.RefreshAsync().ConfigureAwait(true)
                ? "Friends updated."
                : "Could not reach the relay; showing saved data.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sends a friend request to the entered code.</summary>
    /// <returns>A task that completes when the relay has accepted the request.</returns>
    [RelayCommand(CanExecute = nameof(CanSendRequest))]
    private async Task SendRequestAsync()
    {
        ClearError();
        IsBusy = true;

        try
        {
            var code = FriendCodeContract.Normalize(FriendCodeInput);
            await _friends.SendRequestAsync(code).ConfigureAwait(true);

            FriendCodeInput = string.Empty;
            StatusText = "Request sent.";
        }
        catch (RelayApiException ex)
        {
            // The relay's message is written for the user — "you are already
            // friends", "that is not a valid friend code" — so it is shown as is.
            SetErrorMessage(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending a friend request failed.");
            SetErrorMessage("The request could not be sent.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Accepts an incoming request.</summary>
    /// <param name="entry">The request to accept.</param>
    /// <returns>A task that completes when the answer has been recorded.</returns>
    [RelayCommand]
    private Task AcceptAsync(FriendListEntry? entry) => RespondAsync(entry, accept: true);

    /// <summary>Rejects an incoming request.</summary>
    /// <param name="entry">The request to reject.</param>
    /// <returns>A task that completes when the answer has been recorded.</returns>
    [RelayCommand]
    private Task RejectAsync(FriendListEntry? entry) => RespondAsync(entry, accept: false);

    /// <summary>Answers an incoming request.</summary>
    /// <param name="entry">The request being answered.</param>
    /// <param name="accept">Whether to accept it.</param>
    private async Task RespondAsync(FriendListEntry? entry, bool accept)
    {
        if (entry is null)
        {
            return;
        }

        ClearError();
        IsBusy = true;

        try
        {
            await _friends.RespondToRequestAsync(entry.FriendCode, accept).ConfigureAwait(true);
            StatusText = accept ? $"{entry.DisplayName} added." : "Request declined.";
        }
        catch (RelayApiException ex)
        {
            SetErrorMessage(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Responding to a friend request failed.");
            SetErrorMessage("The response could not be sent.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Republishes the list when the service reports a change.</summary>
    /// <param name="sender">The friends service.</param>
    /// <param name="e">Unused.</param>
    private void OnFriendsChanged(object? sender, EventArgs e) => RefreshEntries();

    /// <summary>Reflects a connection state change into the banner.</summary>
    /// <param name="sender">The friends service.</param>
    /// <param name="change">The new state.</param>
    private void OnConnectionStateChanged(object? sender, RelayConnectionStateChanged change) =>
        ApplyConnectionState(change.State, change.Detail);

    /// <summary>Copies the current list into the bound collection.</summary>
    private void RefreshEntries()
    {
        Entries = new ObservableCollection<FriendListEntry>(_friends.Friends);
        IsEmpty = Entries.Count == 0;
    }

    /// <summary>Updates the banner text and command availability for a state.</summary>
    /// <param name="state">The new connection state.</param>
    /// <param name="detail">Optional explanation from the connection layer.</param>
    private void ApplyConnectionState(RelayConnectionState state, string? detail)
    {
        ConnectionState = state;
        IsUsingCachedData = state != RelayConnectionState.Connected;

        ConnectionText = state switch
        {
            // Distinguished from "offline" on purpose: no relay configured is a
            // setting the user has not filled in, not something being wrong.
            RelayConnectionState.Disabled =>
                "Friends are turned off. Add a relay address in Settings to use them.",
            RelayConnectionState.Connecting => "Connecting to the relay…",
            RelayConnectionState.Reconnecting => detail ?? "Reconnecting to the relay…",
            RelayConnectionState.Offline =>
                detail ?? "The relay is not reachable. Showing saved friends; this will reconnect on its own.",
            _ => string.Empty
        };

        OnPropertyChanged(nameof(CanSendRequest));
        OnPropertyChanged(nameof(IsRelayUnconfigured));
        SendRequestCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-evaluates whether the entered code can be sent.</summary>
    /// <param name="value">The new input.</param>
    partial void OnFriendCodeInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanSendRequest));
        SendRequestCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Detaches from the friends service.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _friends.FriendsChanged -= OnFriendsChanged;
        _friends.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
