"use strict";

const StoreAPI = (() => {
    const API_BASE = "/api";
    const CORRELATION_ID_HEADER = "X-Correlation-ID";

    async function request(path, options = {}) {
        const correlationId = crypto.randomUUID();
        const response = await fetch(`${API_BASE}${path}`, {
            ...options,
            headers: {
                "Accept": "application/json",
                [CORRELATION_ID_HEADER]: correlationId,
                ...options.headers
            }
        });

        const contentType = response.headers.get("content-type") ?? "";
        const body = contentType.includes("application/json")
            ? await response.json()
            : await response.text();

        if (!response.ok) {
            const message = body?.message
                ?? body?.detail
                ?? body?.title
                ?? `Store API Fehler: HTTP ${response.status} ${response.statusText}`;
            const error = new Error(message);
            error.correlationId = response.headers.get(CORRELATION_ID_HEADER) ?? correlationId;
            throw error;
        }

        return response.status === 204 ? null : body;
    }

    async function getFeaturedProducts() {
        return await request("/products/featured", { method: "GET" });
    }

    async function checkout(items) {
        if (!Array.isArray(items) || items.length === 0) {
            throw new Error("Der Warenkorb ist leer.");
        }

        return await request("/checkout", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ items })
        });
    }

    return Object.freeze({
        getFeaturedProducts,
        checkout
    });
})();
