const API_BASE="https://localhost:5001/api";
export async function get(url){
 const r=await fetch(`${API_BASE}${url}`);
 if(!r.ok) throw new Error("API Error");
 return r.json();
}
