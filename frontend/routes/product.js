var proxy = require('express-http-proxy');
var endpoints = require('../consul/serviceLocation');

module.exports = proxy(
    () => endpoints.getServiceLocationPath('product-service'),
    {
        memoizeHost: false,

        proxyReqPathResolver: function (req) {
            // req.originalUrl preserves the full /products/... path and any query string
            console.log(`[BFF][products] → ${req.method} ${req.originalUrl}`);
            return req.originalUrl;
        },

        proxyReqOptDecorator: function (proxyReqOpts, srcReq) {
            if (srcReq.headers.authorization) {
                proxyReqOpts.headers['Authorization'] = srcReq.headers.authorization;
            }
            return proxyReqOpts;
        },

        userResDecorator: function (proxyRes, proxyResData, userReq) {
            console.log(`[BFF][products] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.originalUrl}`);
            return proxyResData;
        },

        proxyErrorHandler: function (err, res, next) {
            console.error(`[BFF][products] ✗ proxy error | upstream: ${endpoints.getServiceLocationPath('product-service')} | ${err.message}`);
            next(err);
        }
    }
);