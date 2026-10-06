# Historial de versiones

Resumen de lo que fue entrando en cada versión. Las primeras (1.0 a 1.3) se agrupan porque fueron
iteraciones de la base; a partir de 1.4 el detalle es completo.

## 1.0 → 1.3 — la base

- Monitor de conexiones por proceso con P/Invoke sobre `iphlpapi` (`GetPerTcpTable` /
  `GetExtendedTcpTable`), sin librerías externas.
- Alerta flotante en la primera conexión de cada aplicación, con Permitir / Bloquear.
- Reglas en Windows Firewall con el prefijo `MRPCJ:` y el grupo `Monitor de Red PCJ`.
- **1.1.0 — bloqueo total**: la política de salida pasa a `BlockOutbound` y solo salen las apps
  con regla de permiso. Se decide app por app en el asistente «Quién puede salir a internet».
- **1.1.2**: corregida la sintaxis de `netsh` para cambiar la política de salida.
- Inventario completo de aplicaciones (histórico + en ejecución) con grupo de sistema y de usuario.
- Gráficas de tráfico en tiempo real, pestaña de Recursos y temperatura del disco M.2.
- Modo incógnito (no graba histórico), la X manda a bandeja en vez de cerrar, arranque elevado por
  tarea programada sin preguntar UAC cada vez.
- Menú de bandeja rediseñado como `Popup` propio (un `ContextMenu` de WPF no admite transparencias)
  con retiro automático al sacar el puntero o hacer clic fuera.

## 1.4 — Más herramientas

- Catálogo de doce herramientas con descripción y guía en cada ficha: radar de conexiones, firmas
  de procesos, cuota, tráfico por país, perfiles de firewall, fugas de DNS, puertos expuestos,
  dispositivos de la LAN, informe, escáner y registros.
- Escáner de red y registros se mueven dentro de «Más herramientas», y desde Ajustes se puede
  elegir qué herramienta ocupa la pantalla principal.
- «Tráfico por país» con consulta en línea opcional (`opt-in`), en lote y ordenada.
- **1.4.1**: el buscador de Protección deja de ir a tirones. La medida (`--search-bench`) sacó que
  los ~340 ms por tecla estaban en `TrafficService.GetApps`, no en el dibujado de la lista.
- Barra de PESO explicada con un tooltip que dice el porcentaje real.

## 1.5 — Motor de temas

- Paleta de claves (`B.*`, `C.*`, `R.*`, `F.*`, `Sz.*`) y todos los estilos enlazados con
  `DynamicResource`, para que el tema cambie en caliente sin reiniciar.
- Tres paletas: **Aurora** (clara), **Nocturna** (oscura) y **Bruma**. `--theme-check` verifica que
  las tres tengan las mismas 150 claves.
- Rediseño completo de Protección (filas, mandos, animaciones) y de Configuración, con pestaña de
  Temas.
- **1.5.1**: puertos expuestos cruzados con las reglas del firewall y botón «Bloquear entrada»;
  perfil de firewall rápido en la franja de Protección.
- **1.5.2**: panel flotante de perfiles en Protección (guardar, aplicar, borrar, volver al modo
  normal).

## 1.6 — Excepciones, malware y la lista viva

- **1.6.0** — Ajustes gana la sección «Excepciones del sistema»: quince apartados que abren cada
  uno lo que el bloqueo total corta por error (DHCP, DNS, Windows Update, Tienda, escritorio
  remoto, VPN, descubrimiento de redes, …), más el interruptor de **red local** (deja hablar con
  impresoras, NAS y Chromecast sin abrir internet) y la **lista de bloqueos de malware**, que se
  baja de una fuente pública y se escribe en el fichero `hosts` de Windows dejando copia del
  anterior en `hosts.pcj-copia`. También: «Configuración adicional» por aplicación y control de
  procesos hijo (heredan el permiso de quien los lanza).
- **1.6.1** — la descarga del malware se ve en directo: ventana `MalwareWindow` con bytes, líneas
  leídas, dominios sacados, cancelación y copia guardada, más el botón «Ver el proceso» en Ajustes.
- **1.6.2** — dos cosas. Las reglas del firewall se casan con la **ruta real** del ejecutable
  (`ReglaApuntaA`, `RutaRegla`, `InvalidarReglas`) y `ReparaReglaPropia()` reescribe al arrancar la
  regla propia si apunta a otra copia: antes, dos instalaciones con el mismo nombre de exe
  compartían permiso y la copia de `bin\` se lo dejaba sin salida a la instalada. Y la pestaña de
  Protección pasa a listar solo las apps vivas o que pidieron internet, con ventana de minutos y
  los cuatro recuentos convertidos en filtros.
- **1.6.3** — el extremo izquierdo de la barra de minutos pasa a ser **«en vivo»** (repaso de
  procesos en cada sondeo). Y se arregla el «100 %» con las cifras a medias en el panel de la
  descarga: la fase de lectura escribe ahora los números finales y el 100 % solo se declara si los
  bytes bajados cuadran con el total; si no, la barra se queda en su proporción real y avisa de
  lista incompleta.
- **1.6.4** — el chip de la ventana de minutos ya no puede montarse sobre el selector de grupo en
  la lista de Protección. Además de pasar el `StackPanel` a `Grid`, hay un colchón fijo de 22 px y
  `AflojaCabecera()` suelta lastre por pasos según lo que quepa de verdad (esconde la palabra
  «Refrescar», luego encoge barra de minutos y buscador).
- **1.6.5** — la barra de progreso de la ventana de la lista de malware se llena de verdad. Al
  llegar al 100 % empataba a estrella contra la columna del hueco —una columna `*` en WPF vale
  `1*`— y se pintaba a media pista; `RellenarBarra(f)` ahora mueve las dos columnas.
- **1.6.6** — en «Excepciones del sistema» cada interruptor lleva debajo la palabra **bloqueo
  activo** o **bloqueo inactivo**, y las quince fichas salen contraídas con un botón «ver la
  explicación» que saca para qué sirve cada una y qué reglas escribe. La polaridad se dijo por
  escrito porque no era evidente: en las fichas encendido significa *dejar pasar*, y solo en la
  lista de malware encendido significa *cortar*.
- **1.6.7** — «Preguntar para conectar» pregunta de verdad por todo. Al poner el corte, lo que ya
  estaba abierto se salvaba de la pregunta (`_launchPrev` se sembraba para no avisar por lo
  existente), así que en un PC recién instalado esas apps quedaban **bloqueadas sin haber sido
  consultadas nunca** —la queja del autor en el segundo equipo—. Ahora `BarridaDeAbiertos()` barre
  los procesos abiertos sin decisión y los mete en la cola de avisos `_colaAviso`, y la cola
  **suelta un solo aviso cada vez** (`SoltarAviso()`), porque con el corte puesto hay decenas a la
  vez y sin cola saldrían veinte ventanas montadas en la misma esquina. La barrida se repite al
  cambiar de modo y al relanzar el monitor, y excluye lo que no se puede preguntar: dentro de
  `C:\Windows`, el propio PCJ y quien ya tenga decisión puesta. Sumado: la franja de Protección
  dice «*N* avisos esperando respuesta», cada aviso avisa de cuántos quedan detrás, y el botón
  **«Repreguntar»** devuelve a la cola los abiertos sin decidir. Comprobación nueva
  `--cola-check parte.txt [strict]`, que verifica el filtro de la barrida y que los avisos salen de
  uno en uno.
- **1.6.8** — **«Borrar todas las decisiones»**, como cuarta opción del menú de los modos (donde
  está «Preguntar para conectar»). Venía de la pregunta del autor: *¿cómo reseteo las reglas para
  que vuelva a preguntar por todas?* hasta entonces la única receta era a mano —quitar el corte,
  salir, borrar `apps.json.rules` y `apps.json`, volver—. `FirewallService.ForgetDecisions()`
  quita las reglas `MRPCJ:` de aplicación (permisos de salida y de entrada y bloqueos sueltos) y
  vacía los marcadores; `TrafficService.OlvidarMarcasDeApps()` deja además las fichas como nunca
  contestadas. Se conservan a propósito **la regla de salida del propio monitor** —sin ella PCJ no
  puede bajar la lista de malware—, las excepciones del sistema (`EXC`), los bloqueos por puerto,
  la «Configuración adicional» y todo el tráfico medido. Antes de tocar nada, la ventana de
  confirmación dice las cifras exactas (marcas guardadas, reglas que se van, apps que vuelven a
  preguntar) y avisa si está en «Bloquear a todos», porque ahí nadie volvería a preguntar. Con el
  corte y los avisos puestos, los abiertos se rearman enseguida con `VolverAPreguntarAbiertos()`.
  Operación elevada nueva `--fw-forget` (con su espera larga, como `--fw-strict`). Comprobaciones:
  `--modos-preview modos.png` fotografía el desplegable entero sacándolo del `Popup`, y
  `--protect-window-check` verifica que son cuatro opciones y que la última no lleva número de
  modo. El parte de `--cola-check` añade el bloque «Decisiones guardadas (solo lectura)» y, de
  paso, se arregló su propia prueba de la cola: las tres rutas falsas las borraba el repaso de
  procesos siguiente (`_runningExes` se reconstruye desde lo que hay en el equipo), así que ahora
  se recuerdan en `_ficticios` y el aviso de uno en uno por fin se puede comprobar con `strict`.
- **1.6.9** — La banda de mando deja de montarse sobre los botones. La queja del autor, con foto:
  el chip «en directo» salía **encimado** del botón «Elegir apps». La identidad de la banda era una
  `StackPanel` horizontal dentro de una columna `*`, y una pila no recorta: cuando los cinco mandos
  aprietan, el título y su chip se salen de la columna y pintan debajo de los botones —a 1100 px se
  tocaban, a los 900 el título quedaba en «Protec»—. Ahora la identidad es una rejilla (escudo
  entero + texto que cede, con `ClipToBounds` de red) y `AflojaBanda()` suelta lastre por pasos,
  con los anchos reales de cada pieza y no con umbrales a ojo, igual que hace años que hace la
  cabecera de la lista: **paso 1** la palabra «Bloqueo total» (el interruptor se queda su «Activo»
  pegado y el botón del modo ya dice qué modo está puesto), **paso 2** el chip «en directo» (el
  punto verde que late dentro del escudo ya dice lo mismo), **paso 3** el título pasa a
  «Protección». A 1200 px o más no se suelta nada. Los mandos cambian solos de ancho —«Repreguntar»
  sale según el modo y la etiqueta del botón mide distinta en cada modo—, por eso avisan
  `BandaGrid.SizeChanged` y `BandaDer.SizeChanged`. Las cuentas se hacen siempre con las piezas
  puestas (con las escondidas el repaso creería que sobra sitio y devolvería la palabra, y la banda
  parpadearía). Comprobación: `--protect-window-check` recorre 900, 950, 1000, 1050, 1100, 1200 y
  1400 px midiendo el borde derecho de lo último que enseña la identidad contra el borde izquierdo
  de «Elegir apps» y si el título se lee entero, y `--protect-preview` añade al vuelco
  `.cabecera.txt` las mismas dos cifras.

## 1.6.9 — Limpieza para publicar el código

Preparación del repositorio para publicarlo como software libre. **No cambia ninguna
funcionalidad, ni la interfaz, ni el comportamiento del firewall**: ningún método, ninguna cadena
que vea el usuario en las pantallas y ninguna regla del firewall se han tocado. La versión sigue
siendo 1.6.9.

- **Autoría**: los comentarios que nombraban a la persona pasan a decir «el autor». Son
  observaciones de pruebas hechas a mano (`ProtectView`, `SettingsWindow`, `TrayMenuBody`,
  `MalwareWindow`, `AjusteAppWindow`, `AppExcepciones`) y una cadena del parte de `--tools-check`.
- **Rutas personales**: `tools/gen-icon.ps1` escribía `app.ico` en una ruta absoluta de un equipo
  concreto; ahora la calcula desde la carpeta del propio guion (`Split-Path` + `Join-Path`). El
  icono regenerado sale byte a byte igual, así que se puede comprobar que el arreglo no cambia
  nada.
- **Licencia**: el aviso de `MonitorRedPCJ.csproj` pasa a `Copyright (c) 2026 PCJ — Monitor de Red
  PCJ` (es metadata del ensamblado; no afecta al comportamiento). Se añade `LICENSE` en el formato
  canónico de MIT para que GitHub la reconozca, con una nota que aclara que la licencia cubre el
  código y no los datos de los servicios externos. Se retira `LICENCIA.md`, que quedaba duplicado.
- **README**: reescrito para un lector que no conoce el proyecto. Añade **Privacidad** (qué
  funciona en local y qué sale del equipo), **Servicios externos** (las dos URLs, qué se baja,
  dónde se guarda, cuándo, qué pasa si falla y bajo qué condiciones de uso), **Seguridad** (el
  programa es una herramienta adicional, no sustituye al antivirus), **Requisitos**,
  **Instalación** y **Reportar problemas**, y un resumen en inglés. Corrige el enlace de licencia,
  que apuntaba al fichero retirado.
- **`.gitignore`**: se amplía con `publish/`, `*.binlog`, `*.err`, `Test Results/`, `Thumbs.db`,
  `desktop.ini` y restos de editor, y deja escrito qué ficheros **no** se ignoran a propósito y
  por qué.
- **Borrado**: `instalacion.log` del paquete entregado (registro de una máquina concreta), la
  `LICENCIA.md` duplicada y una copia suelta de `src/MonitorRedPCJ/assets/` que había quedado
  fuera de las tres carpetas del proyecto por la ruta absoluta del guion del icono.

### Cierre: la pantalla «Acerca de» y el instalador definitivo

Dos cosas que quedaron pendientes en la limpieza y que se cierran ahora. Tampoco cambian el
comportamiento del programa ni del firewall.

- **Ajustes → Acerca de**: la tarjeta del programa decía «Software de uso personal. Sin pagos, sin
  licencias y sin enviar nada fuera». Ya no es exacto: el programa es público, lleva licencia MIT
  y tiene dos funciones opcionales que consultan servicios externos. El texto pasa a decir
  «Software gratuito y de código abierto, con licencia MIT. Autor: PCJ. El funcionamiento es local;
  solo algunas funciones opcionales, apagadas por defecto, consultan servicios externos para
  actualizar la lista de protección o saber el país de cada IP.». Al escribirlo se cortaba por el
  borde de la ventana: la tarjeta usaba `StackPanel Orientation="Horizontal"`, que da a los hijos
  ancho infinito y por eso el `TextWrapping="Wrap"` del aviso nunca entraba en acción. Se cambia a
  una `Grid` de dos columnas (`44` + `*`), que es el mismo dibujo —icono de 44 px y 13 px de
  aire— pero deja que el texto envuelva dentro de la tarjeta, como en las demás. Comprobado con
  `--settings-preview acerca.png 6`.
- **Instalador** (`installer/setup.iss`): se le añade `AppCopyright` con el aviso de licencia y
  `LicenseFile=../LICENSE`, que mete la pantalla «Acuerdo de Licencia» al principio del asistente,
  antes de elegir carpeta. Es una página más: no se toca ninguna otra parte del flujo, ni las
  tareas, ni la copia de archivos, ni el registro de la tarea programada. Los datos del paquete
  quedan en Nombre «Monitor de Red PCJ», Versión «1.6.9» (no sube), Autor «PCJ» y Copyright
  «Copyright (c) 2026 PCJ — Monitor de Red PCJ», y son los mismos que enseña el `.exe` y el
  `.dll` compilados.

### Dos mejoras preventivas antes de publicar

Revisión final sobre el `.gitignore` y sobre cómo se explica la licencia. No se tocó código, ni
diseño, ni comportamiento del firewall, ni la versión.

- **`.gitignore`**: se añaden exclusiones para que nada de uso local pueda colarse en una futura
  versión del repositorio —restos de editor (`*.bak`, `*~`, `*.log`), carpetas de VS Code e IntelliJ
  (`.vscode/`, `.idea/`, `*.code-workspace`), credenciales y claves (`*.key`, `*.pem`, `*.pfx`,
  `*.p12`, `*.snk`, `*.token`, `*.secret`, `secrets.json`, `.env*`) y los ficheros de datos que el
  propio programa escribe en `%AppData%\MonitorRedPCJ` (`settings.json`, `apps.json`,
  `malware-lista.txt`, `geo-cache.json`, `geo-vistos.json`, `hosts.pcj-copia`), que si se copian al
  proyecto publicarían el histórico y las rutas de un equipo concreto. Se comprueba con un
  `git add -A` de prueba sobre una copia del árbol: los **86** ficheros del repositorio se
  versionan igual que antes y la lista de ignorados sale vacía, o sea que ninguna regla nueva tapa
  nada necesario para compilar. Se deja escrito además lo que **no** se ignora a propósito
  (`LICENSE`, `README.md`, `CAMBIOS.md`, `src/`, `installer/`, `tools/`, `capturas/`).
- **Licencia y datos de terceros**: el resumen en español de `LICENSE`, la sección «Licencia» del
  README y su resumen en inglés dicen ahora las cinco cosas con las mismas palabras en los tres
  sitios: que la MIT cubre **solo el código** del repositorio; que la lista de urlhaus y las
  respuestas de ip-api **son propiedad de sus proveedores** y no obra de PCJ; que **PCJ no reclama
  ningún derecho sobre esas bases de datos**; que **la MIT no otorga derechos sobre datos de
  terceros**; y que quien active esas funciones o quiera redistribuir sus resultados **tiene que
  respetar las condiciones de cada servicio**. La explicación técnica de los dos servicios (URL,
  cuándo se baja, dónde se guarda, qué pasa si falla) no cambia: ya era correcta.



