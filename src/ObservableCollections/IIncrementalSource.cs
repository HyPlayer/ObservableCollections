// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ObservableCollections;

/// <summary>
/// Represents a data source whose items can be loaded incrementally.
/// </summary>
/// <typeparam name="TSource">The collection element type.</typeparam>
public interface IIncrementalSource<TSource>
{
    /// <summary>
    /// Retrieves one page of items.
    /// </summary>
    /// <param name="pageIndex">The zero-based page index.</param>
    /// <param name="pageSize">The requested number of items.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    Task<IEnumerable<TSource>> GetPagedItemsAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
