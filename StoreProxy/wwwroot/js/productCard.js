"use strict";

function createProductCard(product, onBuy) {
    const card = document.createElement("article");
    card.className = "product-card";
    card.dataset.productId = product.id;

    if (product.image) {
        const image = document.createElement("img");
        image.src = product.image;
        image.alt = product.name ?? "Produkt";
        card.appendChild(image);
    }

    const title = document.createElement("h3");
    title.textContent = product.name ?? "Unbekanntes Produkt";
    card.appendChild(title);

    const price = document.createElement("p");
    price.className = "price";
    price.textContent = typeof product.price === "number"
        ? product.price.toLocaleString("de-DE", { style: "currency", currency: "EUR" })
        : "Preis nicht verfügbar";
    card.appendChild(price);

    const stock = document.createElement("p");
    stock.className = product.isSoldOut ? "stock sold-out" : "stock available";
    stock.textContent = product.isSoldOut
        ? "Ausverkauft"
        : `${product.availableQuantity} Stück auf Lager`;
    card.appendChild(stock);

    const buyButton = document.createElement("button");
    buyButton.type = "button";
    buyButton.className = "buy-button";
    buyButton.textContent = product.isSoldOut ? "Ausverkauft" : "Kaufen";
    buyButton.disabled = product.isSoldOut;
    buyButton.setAttribute("aria-label", product.isSoldOut
        ? `${product.name} ist ausverkauft`
        : `${product.name} in den Warenkorb legen`);
    buyButton.addEventListener("click", () => {
        if (typeof onBuy === "function") {
            onBuy(product);
        }
    });
    card.appendChild(buyButton);

    return card;
}
