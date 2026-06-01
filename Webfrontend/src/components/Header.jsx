import { ShoppingCart } from "lucide-react";

export default function Header() {
  return (
    <header className="header">
      <div className="logo">🌲 HolzStore</div>
      <nav>
        <a href="/produkte">Produkte</a>
        <a href="/angebote">Angebote</a>
        <a href="/zahlung">Zahlungsmöglichkeiten</a>
        <a href="/versand">Versandmöglichkeiten</a>
      </nav>
      <button className="cart-button"><ShoppingCart size={22}/> Warenkorb</button>
    </header>
  );
}
