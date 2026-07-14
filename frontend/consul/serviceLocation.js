var endpoints = {

    "product-service": {
        address: "product-service",
        port: 8080
    },
    "authentication-service": {
        address: "authentication-service",
        port: 8081
    },
    "calendar-service": {
        address: "calendar-service",
        port: 8080
    },
    "notification-service": {
        address: "notification-service",
        port: 8080
    },
    "payment-service": {
        address: "payment-service",
        port: 8080
    },
    "ai-assistant-service": {
        address: "ai-assistant-service",
        port: 8080
    }
};

module.exports = {
    getServicesLocation: function(serviceName)  {
        return endpoints[serviceName];
    },
    getServiceLocationPath: function(serviceName) {
        if(!endpoints[serviceName]) {
            return null;
        }
        let {address, port} = endpoints[serviceName];
        return `http://${address}:${port}`;
    },
    setServiceLocation: function(serviceName, {address,port}) {
        endpoints[serviceName] = {address,port};
    }
};