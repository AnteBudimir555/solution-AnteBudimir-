// Each test class in this assembly boots a full in-process host. Serilog's host integration also
// publishes the created logger to the static Log.Logger, so several hosts starting concurrently race
// over it and log events stop reaching a given host's sinks. Running the classes one at a time keeps
// every host — and its captured log output — isolated, at the cost of a few seconds of wall clock.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
