var proxy = require('express-http-proxy');

// Proxies browser OTLP trace data to the OpenTelemetry Collector.
// The browser SDK sends traces to /otlp/v1/traces, and this route
// forwards them to the collector's OTLP/HTTP endpoint (port 4318).
module.exports = proxy(
    () => process.env.OTEL_COLLECTOR_HTTP_ENDPOINT || 'http://otel-collector:4318',
    {
        memoizeHost: false,
        proxyReqPathResolver: function (req) {
            return '/v1/traces';
        }
    }
);
