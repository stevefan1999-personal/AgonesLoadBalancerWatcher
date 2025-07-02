using System.Net;
using k8s;
using k8s.Autorest;

namespace AgonesLoadBalancerWatcher;

public class Utility
{
    public static async Task<bool> HandleTransientFailureAsStatus(
        Func<Task> action,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await action();
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        { }
        catch (HttpOperationException ex) when (ex.Response.Content.Contains("leader changed")) { }
        catch (KubernetesException ex) when (ex.Status.Code == (int)HttpStatusCode.Gone) { }
        return false;
    }

    public static async Task<(T?, U?)?> ReadModifyWrite<T, U>(
        Func<Task<T?>> fetch,
        Func<T?, Task<U?>> transact,
        uint? failCount,
        bool admitNullForFetchResult = false,
        Func<T?, U?, Task>? committed = null,
        Func<T?, Task>? rollback = null,
        CancellationToken cancellationToken = default
    )
    {
        T? fetched = default;
        U? transacted = default;
        for (uint count = 0; failCount == null || count < failCount; count++)
        {
            if (
                !await HandleTransientFailureAsStatus(
                    async () =>
                    {
                        fetched = await fetch();
                    },
                    cancellationToken
                )
            )
            {
                goto retry;
            }

            if (fetched == null && !admitNullForFetchResult)
            {
                goto retry;
            }

            if (
                !await HandleTransientFailureAsStatus(
                    async () =>
                    {
                        transacted = await transact(fetched);
                    },
                    cancellationToken
                )
            )
            {
                goto retry;
            }

            if (
                committed != null
                && !await HandleTransientFailureAsStatus(
                    () => committed(fetched, transacted),
                    cancellationToken
                )
            )
            {
                goto retry;
            }

            return (fetched, transacted);
            retry:
            if (rollback != null)
            {
                await rollback(fetched);
            }
            continue;
        }
        return null;
    }

    public static async Task<T?> ReadModifyWrite<T>(
        Func<Task<T?>> fetch,
        Func<T?, Task> transact,
        uint? failCount = 30,
        bool admitNullForFetchResult = false,
        Func<T?, Task>? committed = null,
        Func<T?, Task>? rollback = null,
        CancellationToken cancellationToken = default
    )
    {
        T? fetched = default;
        for (uint count = 0; failCount == null || count < failCount; count++)
        {
            if (
                !await HandleTransientFailureAsStatus(
                    async () =>
                    {
                        fetched = await fetch();
                    },
                    cancellationToken
                )
            )
            {
                goto retry;
            }

            if (fetched == null && !admitNullForFetchResult)
            {
                goto retry;
            }

            if (!await HandleTransientFailureAsStatus(() => transact(fetched), cancellationToken))
            {
                goto retry;
            }

            if (
                committed != null
                && !await HandleTransientFailureAsStatus(
                    () => committed(fetched),
                    cancellationToken
                )
            )
            {
                goto retry;
            }

            return fetched;
            retry:
            if (rollback != null)
            {
                await rollback(fetched);
            }
            continue;
        }
        return default;
    }
}
