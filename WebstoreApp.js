"use strict";

/*
 * Zentrale Steuerung der Website.
 *
 * Aufgaben:
 * - Produkte über StoreAPI laden
 * - Produktkarten erzeugen
 * - Benutzeraktionen an StoreAPI weitergeben
 */

document.addEventListener(
    "DOMContentLoaded",
    initializeStore
);


async function initializeStore() {

    await loadFeaturedProducts();

}


/*
 * Lädt die empfohlenen Produkte vom Backend
 * und zeigt sie im vorgesehenen Container an.
 */
async function loadFeaturedProducts() {

    const container =
        document.getElementById("featured-products");

    if (!container) {
        console.error(
            "Container '#featured-products' wurde nicht gefunden."
        );
        return;
    }

    try {

        container.innerHTML =
            '<div class="loading">Produkte werden geladen...</div>';

        const products =
            await StoreAPI.getFeaturedProducts();

        container.innerHTML = "";

        if (!Array.isArray(products) ||
            products.length === 0) {

            container.textContent =
                "Zurzeit sind keine Empfehlungen verfügbar.";

            return;
        }

        for (const product of products) {

            const card =
                createProductCard(
                    product,
                    handleProductSelect
                );

            container.appendChild(card);
        }

    }
    catch (error) {

        console.error(
            "Fehler beim Laden der Produkte:",
            error
        );

        container.textContent =
            "Produkte konnten nicht geladen werden.";
    }
}


/*
 * Wird von productCard.js aufgerufen,
 * sobald der Benutzer ein Produkt auswählt.
 */
async function handleProductSelect(productId) {

    try {

        console.log(
            `Produkt ${productId} wurde ausgewählt.`
        );

        const result =
            await StoreAPI.selectProduct(productId);

        console.log(
            "Antwort des Backends:",
            result
        );

    }
    catch (error) {

        console.error(
            "Fehler bei der Produktauswahl:",
            error
        );

    }
}