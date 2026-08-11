"use strict";

/*
 * Erzeugt die sichtbare Darstellung eines Produktes.
 *
 * Diese Datei kennt weder REST noch YARP noch das Backend.
 * Sie erhält lediglich ein Produktobjekt und eine Funktion,
 * die bei einer Benutzeraktion aufgerufen wird.
 */

function createProductCard(product, onSelect) {

    const card = document.createElement("article");
    card.className = "product-card";

    /*
     * Produktbild
     */
    if (product.image) {

        const image = document.createElement("img");

        image.src = product.image;
        image.alt = product.name ?? "Produkt";

        card.appendChild(image);
    }

    /*
     * Produktname
     */
    const title = document.createElement("h3");

    title.textContent =
        product.name ?? "Unbekanntes Produkt";

    card.appendChild(title);

    /*
     * Preis
     */
    const price = document.createElement("p");
    price.className = "price";

    if (typeof product.price === "number") {

        price.textContent =
            product.price.toLocaleString(
                "de-DE",
                {
                    style: "currency",
                    currency: "EUR"
                }
            );
    }
    else {

        price.textContent = "Preis nicht verfügbar";
    }

    card.appendChild(price);

    /*
     * Aktionsbutton
     *
     * Dieser Button führt selbst keinen HTTP-Aufruf aus.
     * Er informiert lediglich WebstoreApp.js darüber,
     * dass das Produkt ausgewählt wurde.
     */
    const selectButton =
        document.createElement("button");

    selectButton.type = "button";
    selectButton.textContent = "Produkt auswählen";

    selectButton.addEventListener(
        "click",
        () => {

            if (typeof onSelect === "function") {
                onSelect(product.id);
            }

        }
    );

    card.appendChild(selectButton);

    return card;
}