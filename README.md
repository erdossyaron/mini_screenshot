# mini_screenshot

Minimalista képernyőkép-készítő Windowsra. A háttérben fut, és a **PrtSc** gombra indul (Win+PrtSc-re is). RDP alatt is működik.

## Letöltés és indítás

1. Töltsd le a `Screen.exe`-t a [Releases](https://github.com/erdossyaron/mini_screenshot/releases) oldalról.
2. Indítsd el egyszer. Ennyi a telepítés, .NET sem kell hozzá.
3. Az óra mellett megjelenik egy kis ikon és egy értesítés. Innentől a PrtSc gomb ezt az appot indítja.

Az első indításkor az app:
- megkérdezi, **hova mentse a képeket**. Ha nem választasz, ide menti: `Képek\Képernyőképek`.
- átmásolja magát ide: `%LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe`. Így a letöltött fájl nyugodtan törölhető.
- bekapcsolja, hogy **a Windows indulásakor automatikusan elinduljon**.

A mentési mappát később is módosíthatod. Kattints a Másolás gomb melletti **mappa ikonra**, vagy a tálcaikonon jobb gombbal válaszd a **Mentési mappa…** pontot. A mappa ikon fölé állva látod, éppen hova ment.

Az automatikus indulást a tálcaikonon jobb gombbal, az **Indítás a Windows-zal** ponttal kapcsolhatod ki és be. Ha kikapcsolod, az app nem kapcsolja vissza.

**Frissítés:** töltsd le az új `Screen.exe`-t, lépj ki a régiből (tálcaikon → Kilépés), és indítsd el az újat.

**Eltávolítás:** tálcaikon → vedd ki a pipát az **Indítás a Windows-zal** elől → Kilépés. Utána töröld a `%LOCALAPPDATA%\Programs\mini_screenshot` mappát.

> A Windows első indításkor figyelmeztethet („A Windows megvédte a számítógépét”), mert az exe nincs digitálisan aláírva. Ilyenkor kattints a **További információ**, majd a **Futtatás mindenképp** gombra.

## Használat

| Művelet | Hogyan |
|---|---|
| Képernyő befagyasztása | **PrtSc** |
| Vágás | húzd a sarkokat vagy az éleket, vagy kattints kétszer a két sarokpontra |
| Fix képarány | kattints a méretkijelzőre, és válassz arányt (16:9, 4:3, 1:1 …) |
| Mentés és vágólap | **Másolás** gomb vagy **Enter** |
| Mégse | **Esc** vagy jobb klikk |

Semmi nem mentődik, amíg a Másolás gombra nem kattintasz. A képek PNG-ként a választott mappába kerülnek, és egyúttal a vágólapra is.

Ha több monitorod van, mindegyik befagy, és annak a kijelzőnek a vágását menti, ahol a Másolás gombra kattintasz.

### Eszközök

| Eszköz | Billentyű | Megjegyzés |
|---|---|---|
| Vágás | C | alapértelmezett |
| Elmosás | B | a téglalap alatti rész olvashatatlan lesz |
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
