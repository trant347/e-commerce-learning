var express = require('express');
var router = express.Router();
var proxy = require('express-http-proxy');

var endpoints = require('../consul/serviceLocation');

function getHost() {
    return `${endpoints.getServiceLocationPath("calendar-service")}`;
}

module.exports = proxy(getHost,{
    memoizeHost: false,
    proxyReqPathResolver: function (req) {
        const host = endpoints.getServiceLocationPath('calendar-service');
        const updatedPath = req.originalUrl.replace('/calendar-service/', '/');
        console.log(`[BFF][calendar] → ${req.method} ${host}${updatedPath}`);
        return updatedPath;
    },
    userResDecorator: function (proxyRes, proxyResData, userReq) {
        console.log(`[BFF][calendar] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.originalUrl}`);
        return proxyResData;
    },
    proxyErrorHandler: function (err, res, next) {
        console.error(`[BFF][calendar] ✗ proxy error | upstream: ${endpoints.getServiceLocationPath('calendar-service')} | ${err.message}`);
        next(err);
    }
});
