import {get} from "./apiClient";
export const getProducts=()=>get("/products");
export const getProduct=(id)=>get(`/products/${id}`);
