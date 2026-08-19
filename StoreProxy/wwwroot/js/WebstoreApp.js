"use strict";

const storeState = {
    products: [],
    cart: new Map(),
    paymentProviders: [],
    selectedPaymentProvider: null,
    isCheckingOut: false
};

document.addEventListener("DOMContentLoaded", initializeStore);

async function initializeStore() {
    document.getElementById("open-cart")?.addEventListener("click", openCart);
    document.getElementById("close-cart")?.addEventListener("click", closeCart);
    document.getElementById("cart-overlay")?.addEventListener("click", closeCart);
    document.getElementById("checkout-button")?.addEventListener("click", checkoutCart);
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            closeCart();
        }
    });

    renderCart();
    await Promise.all([
        loadFeaturedProducts(),
        loadPaymentProviders()
    ]);
}

async function loadFeaturedProducts() {
    const container = document.getElementById("featured-products");
    if (!container) {
        return;
    }

    container.innerHTML = '<div class="loading">Produkte werden geladen...</div>';

    try {
        const products = await StoreAPI.getFeaturedProducts();
        storeState.products = Array.isArray(products) ? products : [];
        renderProducts();
    }
    catch (error) {
        console.error("Fehler beim Laden der Produkte:", error);
        container.textContent = "Produkte konnten nicht geladen werden.";
    }
}

async function loadPaymentProviders() {
    try {
        const providers = await StoreAPI.getPaymentProviders();
        storeState.paymentProviders = Array.isArray(providers) ? providers : [];
        const selectedProviderStillExists = storeState.paymentProviders.some(provider =>
            provider.key === storeState.selectedPaymentProvider);
        if (!selectedProviderStillExists) {
            storeState.selectedPaymentProvider =
                storeState.paymentProviders.find(provider => provider.key === "demo")?.key
                ?? storeState.paymentProviders[0]?.key
                ?? null;
        }
        renderPaymentProviders();
    }
    catch (error) {
        console.error("Fehler beim Laden der Zahlungsanbieter:", error);
        storeState.paymentProviders = [];
        storeState.selectedPaymentProvider = null;
        showPaymentProviderError("Zahlungsanbieter konnten nicht geladen werden.");
    }

    renderCart();
}

function renderPaymentProviders() {
    const cardsContainer = document.getElementById("payment-provider-cards");
    const optionsContainer = document.getElementById("checkout-payment-providers");
    if (!cardsContainer || !optionsContainer) {
        return;
    }

    cardsContainer.innerHTML = "";
    optionsContainer.innerHTML = "";

    if (storeState.paymentProviders.length === 0) {
        showPaymentProviderError("Zurzeit ist kein Zahlungsanbieter verfügbar.");
        return;
    }

    for (const provider of storeState.paymentProviders) {
        cardsContainer.appendChild(createPaymentProviderCard(provider));
        optionsContainer.appendChild(createPaymentProviderOption(provider));
    }
}

function createPaymentProviderCard(provider) {
    const card = document.createElement("article");
    card.className = "payment-provider-card";

    const heading = document.createElement("div");
    heading.className = "payment-provider-heading";

    const symbol = document.createElement("div");
    symbol.className = "provider-symbol";
    symbol.setAttribute("aria-hidden", "true");
    symbol.textContent = provider.key === "paypal"
        ? "P"
        : provider.key === "stripe" ? "S" : "€";

    const titleContainer = document.createElement("div");
    const label = document.createElement("p");
    label.className = "provider-label";
    label.textContent = "Payment-Provider";
    const title = document.createElement("h3");
    title.textContent = provider.name;
    titleContainer.append(label, title);

    const status = document.createElement("span");
    status.className = "provider-status";
    status.textContent = provider.isTestMode ? "Testbetrieb" : "Verfügbar";

    const description = document.createElement("p");
    description.textContent = provider.isTestMode
        ? "Sicherer Testadapter ohne echte Geldbewegung."
        : "Dieser Zahlungsanbieter ist für den Checkout verfügbar.";

    heading.append(symbol, titleContainer, status);
    card.append(heading, description);
    return card;
}

function createPaymentProviderOption(provider) {
    const label = document.createElement("label");
    label.className = "payment-provider-option";

    const input = document.createElement("input");
    input.type = "radio";
    input.name = "payment-provider";
    input.value = provider.key;
    input.checked = provider.key === storeState.selectedPaymentProvider;
    input.disabled = storeState.isCheckingOut;
    input.addEventListener("change", () => {
        if (input.checked) {
            storeState.selectedPaymentProvider = provider.key;
            showCartMessage(`${provider.name} wurde ausgewählt.`, "");
            renderCart();
        }
    });

    const text = document.createElement("span");
    text.textContent = provider.name;
    label.append(input, text);
    return label;
}

function showPaymentProviderError(message) {
    for (const elementId of ["payment-provider-cards", "checkout-payment-providers"]) {
        const element = document.getElementById(elementId);
        if (element) {
            element.textContent = message;
        }
    }
}

function renderProducts() {
    const container = document.getElementById("featured-products");
    if (!container) {
        return;
    }

    container.innerHTML = "";
    if (storeState.products.length === 0) {
        container.textContent = "Zurzeit sind keine Empfehlungen verfügbar.";
        return;
    }

    for (const product of storeState.products) {
        container.appendChild(createProductCard(product, addProductToCart));
    }
}

function addProductToCart(product) {
    const currentQuantity = storeState.cart.get(product.id) ?? 0;
    if (currentQuantity >= product.availableQuantity) {
        showStoreMessage(`Von ${product.name} ist keine weitere Menge verfügbar.`);
        return;
    }

    storeState.cart.set(product.id, currentQuantity + 1);
    renderCart();
    showStoreMessage(`${product.name} wurde in den Warenkorb gelegt.`);
}

function changeCartQuantity(productId, change) {
    const product = storeState.products.find(item => item.id === productId);
    const currentQuantity = storeState.cart.get(productId) ?? 0;
    const nextQuantity = currentQuantity + change;

    if (!product || nextQuantity <= 0) {
        storeState.cart.delete(productId);
    }
    else if (nextQuantity <= product.availableQuantity) {
        storeState.cart.set(productId, nextQuantity);
    }
    else {
        showCartMessage(`Von ${product.name} sind nur ${product.availableQuantity} Stück verfügbar.`, "error");
    }

    renderCart();
}

function renderCart() {
    const itemsContainer = document.getElementById("cart-items");
    const totalElement = document.getElementById("cart-total");
    const countElement = document.getElementById("cart-count");
    const checkoutButton = document.getElementById("checkout-button");
    if (!itemsContainer || !totalElement || !countElement || !checkoutButton) {
        return;
    }

    itemsContainer.innerHTML = "";
    let total = 0;
    let itemCount = 0;

    for (const [productId, quantity] of storeState.cart) {
        const product = storeState.products.find(item => item.id === productId);
        if (!product) {
            continue;
        }

        total += product.price * quantity;
        itemCount += quantity;
        itemsContainer.appendChild(createCartItem(product, quantity));
    }

    if (itemCount === 0) {
        const emptyMessage = document.createElement("p");
        emptyMessage.className = "empty-cart";
        emptyMessage.textContent = "Ihr Warenkorb ist noch leer.";
        itemsContainer.appendChild(emptyMessage);
    }

    totalElement.textContent = formatPrice(total);
    countElement.textContent = String(itemCount);
    countElement.setAttribute("aria-label", `${itemCount} Artikel`);
    checkoutButton.disabled = itemCount === 0 || storeState.isCheckingOut;
    checkoutButton.disabled ||= storeState.selectedPaymentProvider === null;
    checkoutButton.textContent = storeState.isCheckingOut ? "Zahlung läuft..." : "Bezahlen";
    for (const option of document.querySelectorAll('input[name="payment-provider"]')) {
        option.disabled = storeState.isCheckingOut;
    }
}

function createCartItem(product, quantity) {
    const item = document.createElement("article");
    item.className = "cart-item";

    const heading = document.createElement("div");
    heading.className = "cart-item-heading";
    const title = document.createElement("h3");
    title.textContent = product.name;
    const linePrice = document.createElement("strong");
    linePrice.textContent = formatPrice(product.price * quantity);
    heading.append(title, linePrice);

    const actions = document.createElement("div");
    actions.className = "cart-item-actions";
    const quantityControl = document.createElement("div");
    quantityControl.className = "quantity-control";

    const minusButton = createQuantityButton("−", `Ein ${product.name} entfernen`, () => {
        changeCartQuantity(product.id, -1);
    });
    const quantityText = document.createElement("span");
    quantityText.textContent = String(quantity);
    const plusButton = createQuantityButton("+", `Ein ${product.name} hinzufügen`, () => {
        changeCartQuantity(product.id, 1);
    });
    plusButton.disabled = quantity >= product.availableQuantity;
    quantityControl.append(minusButton, quantityText, plusButton);

    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "remove-button";
    removeButton.textContent = "Entfernen";
    removeButton.addEventListener("click", () => {
        storeState.cart.delete(product.id);
        renderCart();
    });

    actions.append(quantityControl, removeButton);
    item.append(heading, actions);
    return item;
}

function createQuantityButton(label, ariaLabel, onClick) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.setAttribute("aria-label", ariaLabel);
    button.addEventListener("click", onClick);
    return button;
}

async function checkoutCart() {
    if (storeState.cart.size === 0 || storeState.isCheckingOut) {
        return;
    }

    storeState.isCheckingOut = true;
    showCartMessage("Lagerbestand wird reserviert und die Zahlung vorbereitet...", "");
    renderCart();

    try {
        const items = Array.from(storeState.cart, ([productId, quantity]) => ({
            productId,
            quantity
        }));
        const result = await StoreAPI.checkout(
            items,
            storeState.selectedPaymentProvider);

        storeState.cart.clear();
        await loadFeaturedProducts();
        renderCart();
        showCartMessage(
            `${result.message} Gesamt: ${formatPrice(result.total)} · ${result.paymentProvider}`,
            "success");
    }
    catch (error) {
        console.error("Fehler beim Bezahlen:", error);
        showCartMessage(error.message ?? "Der Kauf konnte nicht abgeschlossen werden.", "error");
        await loadFeaturedProducts();
    }
    finally {
        storeState.isCheckingOut = false;
        renderCart();
    }
}

function openCart() {
    const panel = document.getElementById("cart-panel");
    const overlay = document.getElementById("cart-overlay");
    if (!panel || !overlay) {
        return;
    }

    overlay.hidden = false;
    requestAnimationFrame(() => overlay.classList.add("is-visible"));
    panel.classList.add("is-open");
    panel.setAttribute("aria-hidden", "false");
    document.body.classList.add("cart-open");
    document.getElementById("close-cart")?.focus();
}

function closeCart() {
    const panel = document.getElementById("cart-panel");
    const overlay = document.getElementById("cart-overlay");
    if (!panel || !overlay || panel.getAttribute("aria-hidden") === "true") {
        return;
    }

    panel.classList.remove("is-open");
    panel.setAttribute("aria-hidden", "true");
    overlay.classList.remove("is-visible");
    document.body.classList.remove("cart-open");
    window.setTimeout(() => { overlay.hidden = true; }, 200);
    document.getElementById("open-cart")?.focus();
}

function showCartMessage(message, type) {
    const element = document.getElementById("cart-message");
    if (!element) {
        return;
    }

    element.textContent = message;
    element.className = `cart-message ${type}`.trim();
}

let storeMessageTimer;
function showStoreMessage(message) {
    const element = document.getElementById("store-message");
    if (!element) {
        return;
    }

    window.clearTimeout(storeMessageTimer);
    element.textContent = message;
    element.classList.add("is-visible");
    storeMessageTimer = window.setTimeout(() => {
        element.classList.remove("is-visible");
    }, 2200);
}

function formatPrice(value) {
    return Number(value).toLocaleString("de-DE", {
        style: "currency",
        currency: "EUR"
    });
}
