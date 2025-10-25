var endpoints = {

    "product-service": {
        address: "127.0.0.1",
        port: 8080
    },
    "authentication-service": {
        address: "127.0.0.1",
        port: 8081
    },
    "calendar-service": {
        address: "127.0.0.1",
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