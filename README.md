# PersonaCraft

Juega Persona 5 Royal (PC, Steam) como un jugador de Minecraft: movimiento, HUD, inventario y bloques
de Minecraft sobre los mapas de P5R. Es un puerto del diseño de
[SkyCraft](https://github.com/chasmlol/SkyCraft) (Minecraft en Skyrim) y de
[PeakCraft](https://github.com/aeironnsarmiento/PeakCraft) (Minecraft en PEAK): ninguno de los dos
juegos se reescribe; Minecraft corre oculto con el mod de SkyCraft y este mod de Reloaded-II habla
con él por memoria compartida.

Experimental, solo un jugador. Nunca escribe partidas.

> **En pruebas (0.1.0).** Verificado sin el juego: colisión exacta, enlace con Minecraft, bloques,
> HUD y modelo del jugador. En el juego funcionan el control de Joker por Minecraft, la cámara y el
> cambio de sala; la tecla de interactuar (G) y el modelo en tercera persona están arreglados pero aún
> sin probar en partida. Haz copia de tus partidas antes de probarlo y no guardes durante las pruebas.

> Fan project. No está afiliado a Atlus, SEGA, Mojang ni Microsoft; necesitas tener los dos juegos.
> No incluye ningún archivo de ninguno de los dos.

## Instalar y jugar

Necesitas:

- Persona 5 Royal en Steam y [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) con el juego añadido.
- Los mods de Reloaded `p5rpc.lib`, `p5rpc.inputhook`, `reloaded.sharedlib.hooks` y
  `Reloaded.Memory.SigScan.ReloadedII`.
- Minecraft Java 26.3 con Fabric Loader 0.19.5+, Fabric API 0.161.0+26.3, Java 25, el argumento JVM
  `--enable-native-access=ALL-UNNAMED` y el mod de SkyCraft (`skycraft-<versión>.jar`, compilado desde
  [PeakCraft](https://github.com/aeironnsarmiento/PeakCraft) o SkyCraft).

Compilar (necesita el .NET SDK 9 o posterior): `dotnet build p5rpc.personacraft -c Release`. El mod
se compila directamente en `%USERPROFILE%\Reloaded-II\Mods\p5rpc.personacraft` (cámbialo con
`-p:ReloadedModsDir=...`).

Para jugar: abre Minecraft y déjalo en la pantalla de título; luego abre P5R desde Reloaded-II con
PersonaCraft activado y carga una partida.

| Tecla | Hace |
|---|---|
| G | Interactuar en P5R (hablar, puertas, objetos) |
| V | Devolver todo el control a P5R / recuperarlo |
| Tab | Menú de P5R |
| O | Menú de Minecraft |
| F5 | Tercera persona: se ve tu personaje de Minecraft |
| el resto | Minecraft |

Las teclas y opciones están en `personacraft.json`, junto al mod.

## Cómo funciona

| Pieza | Dónde | Cómo |
|---|---|---|
| Enlace | `Link/` | Protocolo de SkyCraft sin cambios (`Local\SkyCraft_v1`), portado de PeakCraft |
| Colisión | `PersonaCraft.Core/GfsCollision.cs`, `World/` | Las mallas `atari*` (当たり) del modelo del mapa `MODEL/FIELD_TEX/Fxxx_yyy_0.GFS`, leídas de `BASE.CPK`. Exactas: coinciden al centímetro con la altura de Joker |
| Coordenadas | `World/Coords.cs` | 100 cm = 1 bloque, mismos ejes; cada mapa en su propia zona del mundo de Minecraft |
| Joker | `Game/FieldPlayer.cs` | Gancho en `fldPCMoveUpdate`: el juego actualiza a Joker (quieto, sin teclado) y luego se escribe la posición de Minecraft en la traslación del modelo |
| Cámara | `Game/FieldPlayer.cs` | `FLD_CAMERA_LOCK` + `FLD_CAMERA_SET_POS/ROT` cada fotograma |
| Teclado | `Input/` | p5rpc.inputhook vacía la lista de teclas de P5R; G = confirmar (E) de P5R, Tab = menú de P5R |
| HUD y bloques | `Render/` | Gancho en `Present` de D3D11 con estado gráfico propio (`SwapDeviceContextState`) |
| Quién manda | `Player/Ownership.cs` | P5R recupera a Joker en diálogos, menús, eventos, combates y cargas |

## Probar sin el juego

- `tools/CollisionCheck`: compara la colisión de un mapa con posiciones registradas.
- `tools/HostSim`: hace de P5R contra Minecraft (colisión real, teletransporte, caminar).
- `tools/RenderTest`: hace que Minecraft coloque bloques y los dibuja con el renderizador del mod.

## Límites conocidos

- Los bloques se dibujan encima de todo: las paredes de P5R aún no los tapan.
- Joker se desliza sin animación de caminar (en primera persona está oculto).
- Las teclas de P5R son las de fábrica (E confirmar, Tab menú); si las cambiaste, ajusta `personacraft.json`.
- Mapas sin malla de colisión (o que no cuadren con Joker) se quedan en manos de P5R.

## Créditos

SkyCraft de chasmlol (MIT) · PeakCraft · p5rpc.lib e inputhook de AnimatedSwine37 · p5r-freecam y
opengfd de rirurin (firma de `fldPCMoveUpdate`, estructura de la cámara) · GFD-Studio de TGE
(referencia del formato GFS) · CriFsV2Lib de Sewer56.
