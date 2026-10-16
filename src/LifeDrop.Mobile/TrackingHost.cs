using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Tracking;
using LifeDrop.Mobile.Visits;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using LifeDrop.Mobile.Auth.Android;
#endif

namespace LifeDrop.Mobile;

// The app's shared services for one signed-in user (steps 1.13, 1.14): the shift tracker, the visit outbox and the
// API clients, used by the pages and the Android tracking service. Offline data lives in the app's private folder.
public static class TrackingHost
{
    private static readonly object Gate = new();
    private static ShiftTracker? _tracker;
    private static Outbox? _outbox;
    private static IVisitsApiClient? _visits;
    private static ICustomersApiClient? _customers;
    private static IOrdersApiClient? _orders;
    private static IWarehouseApiClient? _warehouse;
    private static IDeliveryApiClient? _delivery;
    private static IPaymentsApiClient? _payments;
    private static ISamplesApiClient? _samples;
    private static IFollowUpsApiClient? _followUps;
    private static IVisitDetailsApiClient? _visitDetails;
    private static ICustomFieldsApiClient? _customFields;
    private static PushTokenSync? _pushSync;
    private static WorkQueue? _workQueue;
    private static WorkExecutor? _workExecutor;
    private static ReferenceCache? _cache;
    private static IDurableStore? _store;
    private static bool _listening;

#if IOS
    private const string PushPlatform = "ios";
#else
    private const string PushPlatform = "android";
#endif

    public static ShiftTracker? Tracker
    {
        get { lock (Gate) return _tracker; }
    }

    // Orders, payments, samples, follow-ups and visit forms kept on the phone without internet (plan step 2.13), and the lists kept for writing an order offline.
    public static WorkQueue? WorkQueue
    {
        get { lock (Gate) return _workQueue; }
    }

    public static ReferenceCache? Cache
    {
        get { lock (Gate) return _cache; }
    }

    public static Outbox? Outbox
    {
        get { lock (Gate) return _outbox; }
    }

    public static IVisitsApiClient? VisitsApi
    {
        get { lock (Gate) return _visits; }
    }

    // Customers and places (step 1.23-b); online only.
    public static ICustomersApiClient? CustomersApi
    {
        get { lock (Gate) return _customers; }
    }

    public static IDeliveryApiClient? DeliveryApi
    {
        get { lock (Gate) return _delivery; }
    }

    public static IVisitDetailsApiClient? VisitDetailsApi
    {
        get { lock (Gate) return _visitDetails; }
    }

    public static ICustomFieldsApiClient? CustomFieldsApi
    {
        get { lock (Gate) return _customFields; }
    }

    public static PushTokenSync? PushSync
    {
        get { lock (Gate) return _pushSync; }
    }

    public static IFollowUpsApiClient? FollowUpsApi
    {
        get { lock (Gate) return _followUps; }
    }

    public static ISamplesApiClient? SamplesApi
    {
        get { lock (Gate) return _samples; }
    }

    public static IPaymentsApiClient? PaymentsApi
    {
        get { lock (Gate) return _payments; }
    }

    public static IWarehouseApiClient? WarehouseApi
    {
        get { lock (Gate) return _warehouse; }
    }

    public static IOrdersApiClient? OrdersApi
    {
        get { lock (Gate) return _orders; }
    }

    public static IDurableStore? Store
    {
        get { lock (Gate) return _store; }
    }

    private static string OfflineFolder => Path.Combine(FileSystem.AppDataDirectory, "offline");

    // Null when the app is not configured (no API address) or the platform is neither Android nor iOS.
    public static ShiftTracker? GetOrCreate()
    {
        lock (Gate)
        {
            if (_tracker is not null) return _tracker;
            var url = Preferences.Get("ApiUrl", "");
            if (string.IsNullOrEmpty(url)) return null;
#if ANDROID || IOS
#if ANDROID
            var credentials = new AndroidSecureCredentialStore(Android.App.Application.Context);
            var clock = new AndroidMonotonicClock();
#else
            var credentials = new IosSecureCredentialStore();
            var clock = new IosMonotonicClock();
#endif
            var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(20) };
            var session = new AuthSession(credentials, new AuthApiClient(http), TimeProvider.System);
            _store = new FileDurableStore(OfflineFolder);
            _visits = new VisitsApiClient(http, session);
            _customers = new CustomersApiClient(http, session);
            _orders = new OrdersApiClient(http, session);
            _warehouse = new WarehouseApiClient(http, session);
            _delivery = new DeliveryApiClient(http, session);
            _payments = new PaymentsApiClient(http, session);
            _samples = new SamplesApiClient(http, session);
            _followUps = new FollowUpsApiClient(http, session);
            _visitDetails = new VisitDetailsApiClient(http, session);
            _customFields = new CustomFieldsApiClient(http, session);
            _pushSync = new PushTokenSync(new PushApiClient(http, session), _store, PushPlatform);
            _outbox = new Outbox(_visits, clock, TimeProvider.System, _store);
            _workQueue = new WorkQueue(TimeProvider.System, _store);
            _workExecutor = new WorkExecutor(_orders, _payments, _samples, _followUps, _visitDetails);
            _cache = new ReferenceCache(_store);
            _tracker = new ShiftTracker(new ShiftApiClient(http, session), TimeProvider.System, new PointQueue(_store));
            if (!_listening)
            {
                // Send what waits on the phone as soon as the internet is back.
                Connectivity.ConnectivityChanged += (_, e) =>
                {
                    if (e.NetworkAccess == NetworkAccess.Internet) _ = SyncQuietlyAsync();
                };
                _listening = true;
            }
            return _tracker;
#else
            return null;
#endif
        }
    }

    // Sends waiting visit steps and tracking points; failures stay on the phone for the next attempt.
    public static async Task SyncQuietlyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (Outbox is { } outbox) await outbox.SyncAsync(cts.Token);
            // Kept work goes after the visit steps, so a visit form never arrives before its check-in.
            WorkQueue? queue;
            WorkExecutor? executor;
            lock (Gate) (queue, executor) = (_workQueue, _workExecutor);
            if (queue is not null && executor is not null) await queue.SyncAsync(executor, id => Outbox?.HasPending(id) == true, cts.Token);
            if (Tracker is { } tracker) await tracker.FlushAsync(cts.Token);
        }
        catch (Exception)
        {
            // Retried on the next connection change, page visit or service tick.
        }
    }

    // After logout the next user must not reuse these services or see this user's offline data.
    public static void Reset()
    {
        lock (Gate)
        {
            _tracker = null;
            _outbox = null;
            _visits = null;
            _customers = null;
            _orders = null;
            _warehouse = null;
            _delivery = null;
            _workQueue = null;
            _workExecutor = null;
            _pushSync = null;
            _customFields = null;
            _cache = null;
            _store = null;
            try
            {
                if (Directory.Exists(OfflineFolder)) Directory.Delete(OfflineFolder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
