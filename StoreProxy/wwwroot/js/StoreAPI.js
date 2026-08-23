"use strict";

const StoreAPI = (() => {
    const API_BASE = "/api";
    const CORRELATION_ID_HEADER = "X-Correlation-ID";

    async function request(path, options = {}) {
        const correlationId = crypto.randomUUID();
        const { timeoutMs = 6500, ...fetchOptions } = options;
        const timeoutController = new AbortController();
        const timeoutHandle = window.setTimeout(
            () => timeoutController.abort(),
            timeoutMs);
        let response;
        try {
            response = await fetch(`${API_BASE}${path}`, {
                ...fetchOptions,
                signal: timeoutController.signal,
                headers: {
                    "Accept": "application/json",
                    [CORRELATION_ID_HEADER]: correlationId,
                    ...fetchOptions.headers
                }
            });
        }
        catch (requestFailure) {
            const timedOut = requestFailure?.name === "AbortError";
            const error = new Error(timedOut
                ? "Der Shop hat nicht rechtzeitig geantwortet."
                : "Der Shop ist derzeit nicht erreichbar.");
            error.status = timedOut ? 504 : 503;
            error.failureKind = timedOut ? "TIMEOUT" : "UNAVAILABLE";
            error.correlationId = correlationId;
            throw error;
        }
        finally {
            window.clearTimeout(timeoutHandle);
        }

        const contentType = response.headers.get("content-type") ?? "";
        const body = contentType.includes("application/json") || contentType.includes("+json")
            ? await response.json()
            : await response.text();

        if (!response.ok) {
            const message = body?.message
                ?? body?.detail
                ?? body?.title
                ?? `Store API Fehler: HTTP ${response.status} ${response.statusText}`;
            const error = new Error(message);
            error.status = response.status;
            error.problem = body;
            error.failureKind = response.status === 504 ? "TIMEOUT" : "UNAVAILABLE";
            error.serviceStatuses = Array.isArray(body?.serviceStatuses)
                ? body.serviceStatuses
                : [];
            error.correlationId = response.headers.get(CORRELATION_ID_HEADER) ?? correlationId;
            throw error;
        }

        return response.status === 204 ? null : body;
    }

    async function getFeaturedProducts() {
        return await request("/products?featured=true", { method: "GET" });
    }

    async function getPaymentProviders() {
        return await request("/payment-providers", { method: "GET" });
    }

    async function getServiceStatuses() {
        return await request("/service-statuses", {
            method: "GET",
            timeoutMs: 5500
        });
    }

    async function createOrder(items, paymentProvider, customerEmail) {
        if (!Array.isArray(items) || items.length === 0) {
            throw new Error("Der Warenkorb ist leer.");
        }
        if (typeof paymentProvider !== "string" || paymentProvider.trim().length === 0) {
            throw new Error("Bitte wählen Sie einen Zahlungsanbieter aus.");
        }
        if (typeof customerEmail !== "string" || customerEmail.trim().length === 0) {
            throw new Error("Bitte geben Sie eine E-Mail-Adresse für die Rechnung an.");
        }

        return await request("/orders", {
            method: "POST",
            timeoutMs: 29000,
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                items,
                paymentProvider,
                customerEmail: customerEmail.trim()
            })
        });
    }

    return Object.freeze({
        getFeaturedProducts,
        getPaymentProviders,
        getServiceStatuses,
        createOrder
    });
})();
