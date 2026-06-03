import { WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import { BatchSpanProcessor } from '@opentelemetry/sdk-trace-web';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { ZoneContextManager } from '@opentelemetry/context-zone';
import { FetchInstrumentation } from '@opentelemetry/instrumentation-fetch';
import { XMLHttpRequestInstrumentation } from '@opentelemetry/instrumentation-xml-http-request';
import { registerInstrumentations } from '@opentelemetry/instrumentation';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { ATTR_SERVICE_NAME } from '@opentelemetry/semantic-conventions';

const resource = resourceFromAttributes({
    [ATTR_SERVICE_NAME]: 'frontend-browser',
});

const provider = new WebTracerProvider({
    resource,
    spanProcessors: [
        new BatchSpanProcessor(
            new OTLPTraceExporter({
                // Sends traces to the BFF, which proxies to the OTel Collector.
                // In production, point this to your collector's OTLP/HTTP endpoint.
                url: '/otlp/v1/traces',
            })
        ),
    ],
});

provider.register({
    contextManager: new ZoneContextManager(),
});

// Auto-instrument fetch() and XMLHttpRequest so every API call
// gets a traceparent header injected automatically.
registerInstrumentations({
    instrumentations: [
        new FetchInstrumentation({
            // Propagate trace context to same-origin requests (BFF)
            propagateTraceHeaderCorsUrls: [/.*/],
            clearTimingResources: true,
        }),
        new XMLHttpRequestInstrumentation({
            propagateTraceHeaderCorsUrls: [/.*/],
        }),
    ],
});

export default provider;
