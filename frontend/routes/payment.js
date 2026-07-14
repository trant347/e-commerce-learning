var express = require('express');
var router = express.Router();
var proxy = require('express-http-proxy');

var endpoints = require('../consul/serviceLocation');

function getHost() {
    return `${endpoints.getServiceLocationPath("payment-service")}`;
}

module.exports = proxy(getHost,{
    memoizeHost: false,
    proxyReqPathResolver: function (req) {
        const host = endpoints.getServiceLocationPath('payment-service');
        const updatedPath = req.originalUrl.replace('/payment-service/', '/');
        console.log(`[BFF][payment] → ${req.method} ${host}${updatedPath}`);
        return updatedPath;
    },
    userResDecorator: function (proxyRes, proxyResData, userReq) {
        console.log(`[BFF][payment] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.originalUrl}`);
        return proxyResData;
    },
    proxyErrorHandler: function (err, res, next) {
        console.error(`[BFF][payment] ✗ proxy error | upstream: ${endpoints.getServiceLocationPath('payment-service')} | ${err.message}`);
        next(err);
    }
});
