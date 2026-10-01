using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.Linq;
using freesnip.foundation.Interfaces;
using log4net;

namespace freesnip.foundation.core
{

    public class SimpleServiceProvider
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(SimpleServiceProvider));
        private readonly object _syncRoot = new object();
        private readonly Dictionary<Type, IList<object>> _services = new();

        public static SimpleServiceProvider Current { get; } = new SimpleServiceProvider();

        public IReadOnlyList<TService> GetAllInstances<TService>()
        {
            var typeOfService = typeof(TService);
            lock (_syncRoot)
            {
                if (!_services.TryGetValue(typeOfService, out var results))
                {
                    return Array.Empty<TService>();
                }

                return results.Cast<TService>().ToArray();
            }
        }

        public TService GetInstance<TService>(bool isOptional = false)
        {
            try
            {
                var instances = GetAllInstances<TService>();

                if (instances.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"Found {instances.Count} instances of {typeof(TService).FullName}, but expected only one."
                    );
                }

                var instance = instances.FirstOrDefault();

                if (!isOptional && instance is null)
                {
                    throw new InvalidOperationException(
                        $"No instance of {typeof(TService).FullName} found, but it is required."
                    );
                }

                return instance;
            }
            catch (Exception ex)
            {
                Log.Error($"GetInstance failed for {typeof(TService)}", ex);
                ExecutionTrace.LogException("SimpleServiceProvider.GetInstance", ex, typeof(TService).FullName);
                throw;
            }
        }

        public void AddService<TService>(IEnumerable<TService> services)
        {
            var serviceType = typeof(TService);
            lock (_syncRoot)
            {
                if (!_services.TryGetValue(serviceType, out var currentServices))
                {
                    currentServices = new List<object>();
                    _services.Add(serviceType, currentServices);
                }

                foreach (var service in services)
                {
                    if (service == null)
                    {
                        continue;
                    }

                    currentServices.Add(service);
                }
            }

            ExecutionTrace.LogEvent("SimpleServiceProvider.AddService", "Completed", serviceType.FullName);
        }

        public void AddService<TService>(params TService[] services)
        {
            AddService(services.AsEnumerable());
        }

        public void RemoveService<TService>(IEnumerable<TService> services)
        {
            var serviceType = typeof(TService);
            lock (_syncRoot)
            {
                if (!_services.TryGetValue(serviceType, out var currentServices))
                {
                    return;
                }

                foreach (var service in services)
                {
                    if (service == null)
                    {
                        continue;
                    }

                    currentServices.Remove(service);
                }

                if (currentServices.Count == 0)
                {
                    _services.Remove(serviceType);
                }
            }

            ExecutionTrace.LogEvent("SimpleServiceProvider.RemoveService", "Completed", serviceType.FullName);
        }

        public void RemoveService<TService>(params TService[] services)
        {
            RemoveService(services.AsEnumerable());
        }
    }
}
