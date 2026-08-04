"use strict";

/*
 * Zentrale Kommunikationsschnittstelle der Website.
 *
 * Die restliche Website kennt ausschließlich StoreApi.
 * Backend-Adressen, Ports und YARP sind für die UI unsichtbar.
 */

const StoreApi = (() => {

    const API_BASE = "/api";

    async function request(path, options = {}) {

        const response = await fetch(
            `${API_BASE}${path}`,
            {
                ...options,
                headers: {
                    "Accept": "application/json",
                    ...options.headers
                }
            }
        );

        if (!response.ok) {

            const message =
                `Store API Fehler: HTTP ${response.status} ${response.statusText}`;

            throw new Error(message);
        }

        // POST-Aktionen dürfen später auch 204 No Content liefern.
        if (response.status === 204) {
            return null;
        }

        const contentType =
            response.headers.get("content-type") ?? "";

        if (contentType.includes("application/json")) {
            return await response.json();
        }

        return await response.text();
    }


    /*
     * READ-Kanal
     *
     * GET /api/products/featured
     */
    async function getFeaturedProducts() {

        return await request(
            "/products/featured",
            {
                method: "GET"
            }
        );
    }


    /*
     * WRITE-/ACTION-Kanal
     *
     * POST /api/products/{id}/select
     */
    async function selectProduct(productId) {

        if (productId === undefined ||
            productId === null ||
            productId === "") {

            throw new Error(
                "Für selectProduct() wurde keine Produkt-ID angegeben."
            );
        }

        const id =
            encodeURIComponent(productId);

        return await request(
            `/products/${id}/select`,
            {
                method: "POST"
            }
        );
    }


    /*
     * Öffentliche Schnittstelle.
     *
     * Nur diese Funktionen sind außerhalb
     * dieser Datei sichtbar.
     */
    return Object.freeze({
        getFeaturedProducts,
        selectProduct
    });

})();