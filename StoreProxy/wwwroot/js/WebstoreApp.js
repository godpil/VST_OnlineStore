"use strict";

const storeState = {
    products: [],
    cart: new Map(),
    paymentProviders: [],
    activePaymentProvider: null,
    customerEmail: "",
    isCheckingOut: false,
    readinessKnown: false,
    isOperational: false,
    serviceStatuses: []
};

let serviceStatusTimer;

document.addEventListener("DOMContentLoaded", initializeStore);

async function initializeStore() {
    document.getElementById("open-cart")?.addEventListener("click", openCart);
    document.getElementById("close-cart")?.addEventListener("click", closeCart);
    document.getElementById("cart-overlay")?.addEventListener("click", closeCart);
    document.getElementById("checkout-button")?.addEventListener("click", checkoutCart);
    document.getElementById("retry-service-status")?.addEventListener("click", async event => {
        const retryButton = event.currentTarget;
        retryButton.disabled = true;
        try {
            await refreshServiceReadiness(true);
        }
        finally {
            retryButton.disabled = false;
        }
    });
    document.getElementById("customer-email")?.addEventListener("input", event => {
        storeState.customerEmail = event.target.value;
        renderCart();
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            closeCart();
        }
    });
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") {
            void refreshServiceReadiness(true);
        }
    });
    window.addEventListener("online", () => void refreshServiceReadiness(true));

    renderCart();
    const operational = await refreshServiceReadiness(false);
    if (operational) {
        await loadStoreData();
    }
    scheduleServiceStatusCheck();
}

async function loadStoreData() {
    await Promise.all([
        loadFeaturedProducts(),
        loadPaymentProviders()
    ]);
}

function scheduleServiceStatusCheck() {
    window.clearTimeout(serviceStatusTimer);
    serviceStatusTimer = window.setTimeout(async () => {
        await refreshServiceReadiness(true);
        scheduleServiceStatusCheck();
    }, 5000);
}

async function refreshServiceReadiness(reloadOnRecovery) {
    const wasOperational = storeState.isOperational;
    try {
        const statuses = await StoreAPI.getServiceStatuses();
        const normalizedStatuses = Array.isArray(statuses) ? statuses : [];
        const operational = normalizedStatuses.length > 0 &&
            normalizedStatuses.every(status => status.available === true);
        applyServiceReadiness(operational, normalizedStatuses, null);

        if (reloadOnRecovery && !wasOperational && operational) {
            await loadStoreData();
            showStoreMessage("Der Shop ist wieder betriebsbereit.");
        }
        return operational;
    }
    catch (error) {
        console.error("Fehler bei der Betriebszustandsprüfung:", error);
        applyServiceReadiness(false, error.serviceStatuses ?? [], error);
        return false;
    }
}

function applyServiceReadiness(operational, statuses, error) {
    storeState.readinessKnown = true;
    storeState.isOperational = operational;
    storeState.serviceStatuses = Array.isArray(statuses) ? statuses : [];

    const banner = document.getElementById("service-status-banner");
    const details = document.getElementById("service-status-details");
    if (banner) {
        banner.hidden = operational;
    }
    if (details) {
        details.textContent = operational
            ? ""
            : createServiceFailureMessage(storeState.serviceStatuses, error);
    }

    document.body.classList.toggle("shop-unavailable", !operational);
    renderProducts();
    renderPaymentProviders();
    renderCart();
}

function createServiceFailureMessage(statuses, error) {
    const unavailable = statuses.filter(status => status.available !== true);
    if (unavailable.length > 0) {
        const serviceDescriptions = unavailable.map(status => {
            const reason = status.failureKind === "TIMEOUT"
                ? "Zeitüberschreitung"
                : "nicht erreichbar";
            return `${status.service}: ${reason}`;
        });
        return `Betroffen: ${serviceDescriptions.join(" · ")}`;
    }

    return error?.status === 504
        ? "Die Betriebszustandsprüfung hat nicht rechtzeitig geantwortet."
        : "Der Betriebszustand der erforderlichen Services konnte nicht bestätigt werden.";
}

function handleOperationalRequestFailure(error) {
    if (Number(error?.status) >= 500) {
        applyServiceReadiness(false, error.serviceStatuses ?? [], error);
    }
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
        handleOperationalRequestFailure(error);
        container.textContent = "Produkte konnten nicht geladen werden.";
    }
}

async function loadPaymentProviders() {
    try {
        const providers = await StoreAPI.getPaymentProviders();
        storeState.paymentProviders = Array.isArray(providers) ? providers : [];
        storeState.activePaymentProvider =
            storeState.paymentProviders.find(provider => provider.isActive === true) ?? null;
        renderPaymentProviders();
    }
    catch (error) {
        console.error("Fehler beim Laden der Zahlungsanbieter:", error);
        handleOperationalRequestFailure(error);
        storeState.paymentProviders = [];
        storeState.activePaymentProvider = null;
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
    }

    if (storeState.activePaymentProvider) {
        optionsContainer.appendChild(
            createActivePaymentProviderDisplay(storeState.activePaymentProvider));
    }
    else {
        optionsContainer.textContent =
            "Der aktive Zahlungsanbieter ist nicht korrekt konfiguriert.";
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
    status.textContent = provider.isActive
        ? "Aktiv konfiguriert"
        : provider.isTestMode ? "Testadapter" : "Verfügbar";

    const description = document.createElement("p");
    description.textContent = provider.isTestMode
        ? provider.isActive
            ? "Dieser Testadapter wird zentral für alle Zahlungen verwendet."
            : "Registrierter Testadapter; derzeit nicht aktiv."
        : provider.isActive
            ? "Dieser Zahlungsanbieter wird zentral für den Checkout verwendet."
            : "Registrierter Adapter; derzeit nicht aktiv.";

    heading.append(symbol, titleContainer, status);
    card.append(heading, description);
    return card;
}

function createActivePaymentProviderDisplay(provider) {
    const label = document.createElement("div");
    label.className = "payment-provider-option active-payment-provider";
    const text = document.createElement("span");
    text.textContent = `${provider.name} · zentral konfiguriert`;
    label.append(text);
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
        const card = createProductCard(product, addProductToCart);
        const buyButton = card.querySelector(".buy-button");
        if (buyButton && !storeState.isOperational) {
            buyButton.disabled = true;
            buyButton.textContent = "Shop nicht verfügbar";
        }
        container.appendChild(card);
    }
}

function addProductToCart(product) {
    if (!storeState.isOperational) {
        showStoreMessage("Der Shop ist derzeit nicht betriebsbereit.");
        return;
    }

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
    checkoutButton.disabled ||= storeState.activePaymentProvider === null;
    checkoutButton.disabled ||= !storeState.readinessKnown || !storeState.isOperational;
    const customerEmailInput = document.getElementById("customer-email");
    checkoutButton.disabled ||= !customerEmailInput?.checkValidity();
    checkoutButton.textContent = storeState.isCheckingOut
        ? "Zahlung läuft..."
        : storeState.isOperational ? "Bezahlen" : "Shop nicht verfügbar";
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
    minusButton.disabled = !storeState.isOperational;
    plusButton.disabled = !storeState.isOperational || quantity >= product.availableQuantity;
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

    if (!storeState.isOperational) {
        showCartMessage(
            "Der Shop ist derzeit nicht betriebsbereit. Es kann keine Bestellung gestartet werden.",
            "error");
        return;
    }

    const customerEmailInput = document.getElementById("customer-email");
    if (!customerEmailInput?.reportValidity()) {
        return;
    }

    // Das Fenster muss noch innerhalb der direkten Benutzeraktion geöffnet
    // werden, sonst blockieren Browser es häufig als Popup.
    const invoiceWindow = window.open("about:blank", "_blank");
    if (invoiceWindow) {
        invoiceWindow.document.title = "Rechnung wird erstellt";
        invoiceWindow.document.body.textContent =
            "Ihre Zahlung wird verarbeitet und die Rechnung wird erstellt...";
    }

    storeState.isCheckingOut = true;
    showCartMessage("Lagerbestand wird reserviert und die Zahlung vorbereitet...", "");
    renderCart();

    try {
        const items = Array.from(storeState.cart, ([productId, quantity]) => ({
            productId,
            quantity
        }));
        const result = await StoreAPI.createOrder(
            items,
            storeState.customerEmail);

        storeState.cart.clear();
        await loadFeaturedProducts();
        renderCart();
        showCartMessage(
            `${result.message} Gesamt: ${formatPrice(result.total)} · ${result.paymentProvider}`,
            "success");
        if (result.invoiceUrl) {
            setInvoiceLink(result.invoiceUrl);
            invoiceWindow?.location.replace(result.invoiceUrl);
        }
        else {
            invoiceWindow?.close();
        }
    }
    catch (error) {
        invoiceWindow?.close();
        console.error("Fehler beim Bezahlen:", error);
        handleOperationalRequestFailure(error);
        showCartMessage(error.message ?? "Der Kauf konnte nicht abgeschlossen werden.", "error");
        await refreshServiceReadiness(false);
        if (storeState.isOperational) {
            await loadFeaturedProducts();
        }
    }
    finally {
        storeState.isCheckingOut = false;
        renderCart();
    }
}

function setInvoiceLink(invoiceUrl) {
    const link = document.getElementById("invoice-link");
    if (!link) {
        return;
    }

    link.href = invoiceUrl;
    link.hidden = false;
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
