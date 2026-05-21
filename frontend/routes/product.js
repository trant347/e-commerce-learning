var express = require('express');
var router = express.Router();
var axios = require('axios');


var endpoints = require('../consul/serviceLocation');


router.get('/', async function(req, res, next){
    try {
        let url = `${endpoints.getServiceLocationPath('product-service')}/products`;
        const { page, limit } = req.query;
        if(page && limit) {
            url += `?page=${page}&limit=${limit}`;
        }
        console.log(`[BFF][products] → GET ${url}`);
        let listOfProducts = await axios.get(url);
        console.log(`[BFF][products] ← ${listOfProducts.status} (${listOfProducts.data.length} items)`);
        res.send(listOfProducts.data);
    } catch (e) {
        const upstream = e.config?.url || 'unknown';
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        next(e);
    }
});


router.get('/:id', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/${req.params.id}`;
    try {
        console.log(`[BFF][products] → GET ${upstream}`);
        let response = await axios.get(upstream, { headers: {"Authorization" : req.get("Authorization")} });
        console.log(`[BFF][products] ← ${response.status}`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        if(e.response && e.response.status == 401) {
            res.sendStatus(401);      
            return;    
        }
        next(e);
    }

});



router.post('/', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products`;
    try {
        console.log(`[BFF][products] → POST ${upstream}`);
        const response = await axios.post(upstream, req.body, {
            headers: { 'Authorization': req.get('Authorization'), 'Content-Type': 'application/json' }
        });
        console.log(`[BFF][products] ← ${response.status} created id=${response.data.id}`);
        res.status(201).send(response.data);
    } catch (e) {
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        next(e);
    }
});

// ── TaskMaster Applications ────────────────────────────────────────────────

// POST /products/applications  — submit application (any authenticated user)
router.post('/applications', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/applications`;
    try {
        console.log(`[BFF][applications] → POST ${upstream}`);
        const response = await axios.post(upstream, req.body, {
            headers: { 'Authorization': req.get('Authorization'), 'Content-Type': 'application/json' }
        });
        console.log(`[BFF][applications] ← ${response.status} id=${response.data.id}`);
        res.status(response.status).send(response.data);
    } catch (e) {
        console.error(`[BFF][applications] ✗ ${e.response?.status || e.code} | ${e.message}`);
        if (e.response) return res.status(e.response.status).send(e.response.data);
        next(e);
    }
});

// GET /products/applications  — list all applications (admin)
router.get('/applications', async (req, res, next) => {
    let url = `${endpoints.getServiceLocationPath('product-service')}/products/applications`;
    if (req.query.status) url += `?status=${req.query.status}`;
    try {
        console.log(`[BFF][applications] → GET ${url}`);
        const response = await axios.get(url, {
            headers: { 'Authorization': req.get('Authorization') }
        });
        console.log(`[BFF][applications] ← ${response.status} (${response.data.length} items)`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][applications] ✗ ${e.response?.status || e.code} | ${e.message}`);
        if (e.response) return res.status(e.response.status).send(e.response.data);
        next(e);
    }
});

// GET /products/applications/:id  — get single application (admin)
router.get('/applications/:id', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/applications/${req.params.id}`;
    try {
        console.log(`[BFF][applications] → GET ${upstream}`);
        const response = await axios.get(upstream, {
            headers: { 'Authorization': req.get('Authorization') }
        });
        console.log(`[BFF][applications] ← ${response.status}`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][applications] ✗ ${e.response?.status || e.code} | ${e.message}`);
        if (e.response) return res.status(e.response.status).send(e.response.data);
        next(e);
    }
});

// PUT /products/applications/:id/accept  — accept application (admin)
router.put('/applications/:id/accept', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/applications/${req.params.id}/accept`;
    try {
        console.log(`[BFF][applications] → PUT ${upstream}`);
        const response = await axios.put(upstream, {}, {
            headers: { 'Authorization': req.get('Authorization') }
        });
        console.log(`[BFF][applications] ← ${response.status} accepted`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][applications] ✗ ${e.response?.status || e.code} | ${e.message}`);
        if (e.response) return res.status(e.response.status).send(e.response.data);
        next(e);
    }
});

// PUT /products/applications/:id/decline  — decline application (admin)
router.put('/applications/:id/decline', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/applications/${req.params.id}/decline`;
    try {
        console.log(`[BFF][applications] → PUT ${upstream}`);
        const response = await axios.put(upstream, req.body, {
            headers: { 'Authorization': req.get('Authorization'), 'Content-Type': 'application/json' }
        });
        console.log(`[BFF][applications] ← ${response.status} declined`);
        res.send(response.data);
    } catch (e) {
        console.error(`[BFF][applications] ✗ ${e.response?.status || e.code} | ${e.message}`);
        if (e.response) return res.status(e.response.status).send(e.response.data);
        next(e);
    }
});

// ── Product image ──────────────────────────────────────────────────────────

router.get('/image/:name', async (req, res, next) => {
    const upstream = `${endpoints.getServiceLocationPath('product-service')}/products/image/${req.params.name}`;
    try {
        console.log(`[BFF][products] → GET ${upstream}`);
        var response = await axios.get(upstream, {
                responseType: "arraybuffer",
                headers: {"Authorization" : req.get("Authorization") || ""}
            });
        console.log(`[BFF][products] ← ${response.status} (image)`);
        var headers = {'Content-Type': 'image/jpeg'};
        res.writeHead(200, headers);
        res.end(response.data, 'binary');


    }catch (e) {
        console.error(`[BFF][products] ✗ ${e.response?.status || e.code} | upstream: ${upstream} | ${e.message}`);
        next(e);
    }
});

module.exports = router;