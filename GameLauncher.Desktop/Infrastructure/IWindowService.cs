using System.Windows;
using GameLauncher.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Maps dialog view models to the windows that host them.
/// </summary>
/// <remarks>
/// Lets a view model ask for "the Add Game dialog" by naming its view model
/// rather than its window. Without this indirection a view model would have to
/// reference a <see cref="Window"/> type, which is the one direction the
/// dependencies are not allowed to point.
/// </remarks>
public sealed class DialogRegistry
{
    private readonly Dictionary<Type, Type> _windowsByViewModel = [];

    /// <summary>
    /// Records that <typeparamref name="TWindow"/> hosts
    /// <typeparamref name="TViewModel"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The dialog's view model.</typeparam>
    /// <typeparam name="TWindow">The window that hosts it.</typeparam>
    /// <returns>The same registry, for chaining.</returns>
    public DialogRegistry Register<TViewModel, TWindow>()
        where TViewModel : DialogViewModelBase
        where TWindow : Window
    {
        _windowsByViewModel[typeof(TViewModel)] = typeof(TWindow);
        return this;
    }

    /// <summary>
    /// Finds the window type registered for a view model.
    /// </summary>
    /// <param name="viewModelType">The dialog view model type.</param>
    /// <returns>The window type that hosts it.</returns>
    /// <exception cref="InvalidOperationException">No window has been registered for that view model.</exception>
    public Type Resolve(Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);

        return _windowsByViewModel.TryGetValue(viewModelType, out var windowType)
            ? windowType
            : throw new InvalidOperationException(
                $"No dialog window is registered for {viewModelType.Name}. " +
                $"Add one in {nameof(ServiceRegistration)}.");
    }
}

/// <summary>
/// Opens modal dialogs resolved from the DI container.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Shows the dialog registered for a view model, modally over the active window.
    /// </summary>
    /// <typeparam name="TViewModel">View model identifying the dialog to open.</typeparam>
    /// <param name="configure">
    /// Optional initialisation applied to the resolved view model before the
    /// window is shown.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the dialog was accepted, <see langword="false"/>
    /// when cancelled, and <see langword="null"/> when it was dismissed without
    /// either.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No window is registered for the view model, or the registered window does
    /// not use it as its data context.
    /// </exception>
    /// <remarks>
    /// The callback exists so a dialog can be told what it is editing without the
    /// caller naming a <see cref="Window"/> type or the view model acquiring a
    /// constructor parameter that only one of its two uses supplies.
    /// </remarks>
    bool? ShowDialogFor<TViewModel>(Action<TViewModel>? configure = null) where TViewModel : DialogViewModelBase;
}

/// <summary>
/// Default <see cref="IWindowService"/>.
/// </summary>
public sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private readonly DialogRegistry _registry;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="services">Container used to resolve windows.</param>
    /// <param name="registry">Maps view models to their windows.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WindowService(IServiceProvider services, DialogRegistry registry)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public bool? ShowDialogFor<TViewModel>(Action<TViewModel>? configure = null)
        where TViewModel : DialogViewModelBase
    {
        var windowType = _registry.Resolve(typeof(TViewModel));

        // Resolved fresh each time: a WPF window cannot be shown again once
        // closed, so dialogs are registered transient and a cached instance would
        // throw on the second open.
        var window = (Window)_services.GetRequiredService(windowType);

        if (configure is not null)
        {
            // Reached through the window rather than resolved separately, because
            // the window built its own view model from the container and a second
            // resolution would configure an instance nothing is bound to.
            if (window.DataContext is not TViewModel viewModel)
            {
                throw new InvalidOperationException(
                    $"{windowType.Name} does not use {typeof(TViewModel).Name} as its data context, " +
                    "so it cannot be configured before being shown.");
            }

            configure(viewModel);
        }

        window.Owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(candidate => candidate.IsActive)
            ?? Application.Current?.MainWindow;

        return window.ShowDialog();
    }
}
