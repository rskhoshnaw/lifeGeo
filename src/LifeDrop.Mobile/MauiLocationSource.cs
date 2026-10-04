using LifeDrop.Mobile.Visits;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// A fresh GPS fix from the phone (plan step 1.12). Never the last known position. The fix is sent as captured,
// with its own time, accuracy and the mock-location flag; the server decides whether it is acceptable.
public sealed class MauiLocationSource : ILocationSource
{
    // A fix without an accuracy is sent as very inaccurate, so the server refuses and records it.
    private const double UnknownAccuracyMeters = 9999;

    public async Task<FixResult> GetFixAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var current = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
                return current == PermissionStatus.Granted ? current : await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            });
            if (status != PermissionStatus.Granted) return FixResult.Failed(FixFailure.PermissionDenied);

            var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15));
            var location = await Geolocation.Default.GetLocationAsync(request, cancellationToken);
            if (location is null) return FixResult.Failed(FixFailure.Unavailable);

            return FixResult.Ok(new DeviceFix(location.Latitude, location.Longitude,
                location.Accuracy is > 0 and var accuracy ? accuracy : UnknownAccuracyMeters,
                location.Timestamp, location.IsFromMockProvider));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FixResult.Failed(FixFailure.TimedOut);
        }
        catch (PermissionException)
        {
            return FixResult.Failed(FixFailure.PermissionDenied);
        }
        catch (Exception e) when (e is FeatureNotEnabledException or FeatureNotSupportedException)
        {
            return FixResult.Failed(FixFailure.Unavailable);
        }
    }
}
