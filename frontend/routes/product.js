var proxy = require('express-http-proxy');
var endpoints = require('../consul/serviceLocation');
var { trace } = require('@opentelemetry/api');

function getTraceId() {
    var span = trace.getActiveSpan();
    if (!span) return 'no-trace';
    return span.spanContext().traceId;
}

module.exports = proxy(
    () => endpoints.getServiceLocationPath('product-service'),
    {
        memoizeHost: false,

        proxyReqPathResolver: function (req) {
            console.log(`[BFF][products] → ${req.method} ${req.originalUrl} | traceId=${getTraceId()}`);
            return req.originalUrl;
        },

        proxyReqOptDecorator: function (proxyReqOpts, srcReq) {
            if (srcReq.headers.authorization) {
                proxyReqOpts.headers['Authorization'] = srcReq.headers.authorization;
            }
            return proxyReqOpts;
        },

        userResDecorator: function (proxyRes, proxyResData, userReq) {
            var traceId = getTraceId();
            // Return traceId in response when the caller didn't send a traceparent header
            // (e.g. direct API usage without browser OTel SDK).
            if (!userReq.headers['traceparent']) {
                proxyRes.headers['x-trace-id'] = traceId;
            }
            console.log(`[BFF][products] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.originalUrl} | traceId=${traceId}`);
            return proxyResData;
        },

        proxyErrorHandler: function (err, res, next) {
            console.error(`[BFF][products] ✗ proxy error | upstream: ${endpoints.getServiceLocationPath('product-service')} | ${err.message} | traceId=${getTraceId()}`);
            next(err);
        }
    }
);