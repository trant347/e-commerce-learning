var express = require('express');
var router = express.Router();
var proxy = require('express-http-proxy');

var endpoints = require('../consul/serviceLocation');

function getHost() {
    return `${endpoints.getServiceLocationPath("authentication-service")}`;
}

module.exports = proxy(getHost,{
    memoizeHost: false,
    proxyReqPathResolver: function (req) {
        const host = endpoints.getServiceLocationPath('authentication-service');
        const updatedPath = req.originalUrl.replace('/user/', '/');
        console.log(`[BFF][auth] → ${req.method} ${host}${updatedPath}`);
        return updatedPath;
    },
    userResDecorator: function (proxyRes, proxyResData, userReq) {
        console.log(`[BFF][auth] ← ${proxyRes.statusCode} ${userReq.method} ${userReq.originalUrl}`);
        return proxyResData;
    },
    proxyErrorHandler: function (err, res, next) {
        console.error(`[BFF][auth] ✗ proxy error | upstream: ${endpoints.getServiceLocationPath('authentication-service')} | ${err.message}`);
        next(err);
    }
});
