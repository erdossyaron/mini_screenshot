# mini_screenshot

Minimalista képernyőkép-készítő Windowsra. A háttérben fut, és a **PrtSc** gombra indul (Win+PrtSc-re is). RDP alatt is működik.

## Letöltés és indítás

1. Töltsd le a `Screen.exe`-t a [Releases](https://github.com/erdossyaron/mini_screenshot/releases) oldalról. Csak ez az egy fájl kell, .NET sem.
2. Indítsd el. Feljön a **telepítő varázsló**:
   - **Hova mentse a képeket?** Alapból: `Képek\Képernyőképek`. A **Tallózás…** gombbal másikat választhatsz.
   - **Indítás a Windows-zal:** alapból be van kapcsolva.
3. Kattints a **Telepítés** gombra. Az óra mellett megjelenik egy kis ikon és egy értesítés. Innentől a PrtSc gomb ezt az appot indítja.

A telepítő az appot ide másolja: `%LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe`. A letöltött fájl ezután nyugodtan törölhető. Rendszergazdai jog nem kell.

Ha az app már fut, és újra elindítod, egy értesítés jelzi, hogy már fut.

A mentési mappát később is módosíthatod. Kattints a **Másol & Ment** gomb melletti **mappa ikonra**, vagy a tálcaikonon jobb gombbal válaszd a **Mentési mappa…** pontot. A mappa ikon fölé állva látod, éppen hova ment.

Az automatikus indulást a tálcaikonon jobb gombbal, az **Indítás a Windows-zal** ponttal kapcsolhatod ki és be. Ha kikapcsolod, az app nem kapcsolja vissza.

**Frissítés:** töltsd le az új `Screen.exe`-t, indítsd el, és a varázslóban kattints a **Frissítés** gombra. A futó régi példányt a varázsló maga állítja le és cseréli le.

**Eltávolítás:** tálcaikon → vedd ki a pipát az **Indítás a Windows-zal** elől → Kilépés. Utána töröld a `%LOCALAPPDATA%\Programs\mini_screenshot` mappát.

> A Windows első indításkor figyelmeztethet („A Windows megvédte a számítógépét”), mert az exe nincs digitálisan aláírva. Ilyenkor kattints a **További információ**, majd a **Futtatás mindenképp** gombra.

## Használat

| Művelet | Hogyan |
|---|---|
| Képernyő befagyasztása | **PrtSc** |
| Vágás | húzd a sarkokat vagy az éleket, vagy kattints kétszer a két sarokpontra |
| Fix képarány | kattints a méretkijelzőre, és válassz arányt (16:9, 4:3, 1:1 …) |
| Vágólapra másolás és mentés | **Másol & Ment** gomb vagy **Enter** |
| Mégse | **Esc** vagy jobb klikk |

Semmi nem mentődik, amíg a **Másol & Ment** gombra nem kattintasz. Ekkor a kép egyszerre kerül a vágólapra és PNG-ként a választott mappába. Utána a jobb alsó sarokban megjelenik a „Másolva & mentve” előnézet. Kattints rá, és megnyílik a fájl az Intézőben. Az **X**-szel bezárhatod.

Ha több monitorod van, mindegyik befagy, és annak a kijelzőnek a vágását menti, ahol a gombra kattintasz.

### Eszközök

| Eszköz | Billentyű | Megjegyzés |
|---|---|---|
| Vágás | C | alapértelmezett |
| Elmosás | B | a téglalap alatti rész olvashatatlan lesz; inverz módban minden el van mosva, csak a téglalap éles (fókusz) |
| Ovál | O | Shift: kör |
| Téglalap | R | Shift: négyzet |
| Vonal / nyíl | L | nyíl az elején vagy a végén, irány cseréje, Shift: 45° |
| Ceruza | P | szabadkézi vonal |
| Szöveg | T | betűméret, háttérszín, átlátszóság; dupla katt: szerkesztés |

A szín piros, fehér vagy fekete lehet.

### Objektumok szerkesztése

- A frissen rajzolt vagy kijelölt objektum **bárhol megfogva mozgatható**.
- A **fehér fogópontokon** méretezhető.
- **Egy kattintás bárhova** leveszi a fókuszt. Utána húzással új objektumot rajzolsz, kattintással kijelölsz egyet.
- **Rétegsorrend:** leghátra, hátrébb, előrébb, legelőre.
- **Delete:** a kijelölt objektum törlése. **Ctrl+Z:** az utolsó objektum visszavonása.

## Fordítás forrásból

.NET 8 SDK kell hozzá.

```powershell
dotnet build -c Release
```

Önálló, egyfájlos exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
