// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Xaml.Data;

namespace ObservableCollections;

/// <summary>
/// An <see cref="ObservableList{T}"/> whose items are loaded one page at a time.
/// This is the CommunityToolkit incremental-loading behavior adapted to
/// ObservableCollections.
/// </summary>
/// <typeparam name="TSource">The incremental source type.</typeparam>
/// <typeparam name="TItem">The collection element type.</typeparam>
public partial class IncrementalLoadingCollection<TSource, TItem> :
    ObservableList<TItem>,
    ISupportIncrementalLoading,
    INotifyPropertyChanged
    where TSource : IIncrementalSource<TItem>
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private bool _isLoading;
    private bool _hasMoreItems = true;
    private CancellationToken _cancellationToken;
    private bool _refreshOnLoad;

    /// <summary>
    /// Initializes a new collection with the supplied source.
    /// </summary>
    public IncrementalLoadingCollection(
        TSource source,
        int itemsPerPage = 20,
        Action? onStartLoading = null,
        Action? onEndLoading = null,
        Action<Exception>? onError = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ItemsPerPage = itemsPerPage;
        OnStartLoading = onStartLoading;
        OnEndLoading = onEndLoading;
        OnError = onError;
    }

    /// <summary>
    /// Raised when an incremental-loading property changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Invoked when a retrieval operation begins.
    /// </summary>
    public Action? OnStartLoading { get; set; }

    /// <summary>
    /// Invoked when a retrieval operation ends.
    /// </summary>
    public Action? OnEndLoading { get; set; }

    /// <summary>
    /// Invoked when a retrieval operation fails.
    /// </summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>
    /// Gets the source used by the collection.
    /// </summary>
    protected TSource Source { get; }

    /// <summary>
    /// Gets the number of items requested for every page.
    /// </summary>
    protected int ItemsPerPage { get; }

    /// <summary>
    /// Gets or sets the zero-based index of the next page.
    /// </summary>
    protected int CurrentPageIndex { get; set; }

    /// <summary>
    /// Gets whether a retrieval operation is running.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
                return;

            _isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
            if (value)
                OnStartLoading?.Invoke();
            else
                OnEndLoading?.Invoke();
        }
    }

    /// <inheritdoc />
    public bool HasMoreItems
    {
        get => !_cancellationToken.IsCancellationRequested && _hasMoreItems;
        private set
        {
            if (_hasMoreItems == value)
                return;

            _hasMoreItems = value;
            OnPropertyChanged(nameof(HasMoreItems));
        }
    }

    /// <inheritdoc />
    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadMoreItemsCoreAsync(count, new CancellationToken(false)).AsAsyncOperation();

    /// <summary>
    /// Clears the collection and returns it to the first page.
    /// </summary>
    public Task RefreshAsync()
    {
        if (IsLoading)
        {
            _refreshOnLoad = true;
        }
        else
        {
            var previousCount = Count;
            Clear();
            CurrentPageIndex = 0;
            HasMoreItems = true;

            if (previousCount == 0)
                return LoadMoreItemsAsync(0).AsTask();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads the page at <see cref="CurrentPageIndex"/>.
    /// </summary>
    protected virtual async Task<IEnumerable<TItem>> LoadDataAsync(
        CancellationToken cancellationToken)
    {
        var result = await Source.GetPagedItemsAsync(
            CurrentPageIndex,
            ItemsPerPage,
            cancellationToken);
        CurrentPageIndex++;
        return result;
    }

    private async Task<LoadMoreItemsResult> LoadMoreItemsCoreAsync(
        uint count,
        CancellationToken cancellationToken)
    {
        uint resultCount = 0;
        _cancellationToken = cancellationToken;

        await _mutex.WaitAsync();
        try
        {
            if (!_cancellationToken.IsCancellationRequested)
            {
                IEnumerable<TItem>? data = null;
                try
                {
                    IsLoading = true;
                    data = await LoadDataAsync(_cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (OnError is not null)
                {
                    OnError(ex);
                }

                if (data is not null && data.Any() && !_cancellationToken.IsCancellationRequested)
                {
                    resultCount = (uint)data.Count();
                    foreach (var item in data)
                        Add(item);
                }
                else
                {
                    HasMoreItems = false;
                }
            }
        }
        finally
        {
            IsLoading = false;

            _mutex.Release();

            if (_refreshOnLoad)
            {
                _refreshOnLoad = false;
                await RefreshAsync();
            }
        }

        return new LoadMoreItemsResult { Count = resultCount };
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
