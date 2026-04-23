# DotnetFleet — Preguntas Frecuentes (FAQ)

Recopilación de las dudas más habituales sobre instalación, operación y
resolución de problemas en DotnetFleet. Si no encuentras la respuesta aquí,
revisa el [README](../README.md) o abre una issue.

---

## Índice

- [Instalación y arranque](#instalación-y-arranque)
- [Tokens, secretos y credenciales](#tokens-secretos-y-credenciales)
- [Workers y descubrimiento](#workers-y-descubrimiento)
- [Servicios systemd](#servicios-systemd)
- [Datos, almacenamiento y caché](#datos-almacenamiento-y-caché)
- [Actualizaciones y rollback](#actualizaciones-y-rollback)
- [Seguridad y red](#seguridad-y-red)
- [Resolución de problemas](#resolución-de-problemas)

---

## Instalación y arranque

### ¿Qué necesito instalado para usar DotnetFleet?

.NET 10 SDK (para `dnx`) o tener instalada la herramienta global
`DotnetFleet.Tool`. En Linux, además, `systemd` si quieres registrar
coordinador/worker como servicios. Los repos que despliegues solo necesitan
`deployer.yaml` en la raíz (el worker invoca `dnx dotnetdeployer.tool -y`, que
se descarga sola).

### ¿Tengo que instalar la tool globalmente o puedo usar `dnx` siempre?

Para uso puntual basta `dnx dotnetfleet.tool ...`. Para servicios de systemd
**sí** se requiere la global tool (`~/.dotnet/tools/fleet`), porque la unidad
necesita un `ExecStart=` con ruta estable. Si llamas a `coordinator install` o
`worker install` desde `dnx`, la propia herramienta detecta esa situación e
instala la global tool por ti.

### ¿Funciona en Windows o macOS?

El comando en primer plano (`fleet coordinator` / `fleet worker`) es
multiplataforma. La instalación como servicio (`install` / `uninstall` /
`status`) es por ahora **solo Linux + systemd**. En otros sistemas usa el
gestor que prefieras (NSSM, launchd, Docker, etc.) apuntando a `fleet
coordinator`.

---

## Tokens, secretos y credenciales

### ¿Cómo puedo saber el token del coordinador para registrar un nuevo worker?

El token se autogenera la primera vez que arrancas el coordinador y se guarda
en su archivo de configuración:

```bash
cat ~/.fleet/coordinator/config.json
```

Busca el campo `registrationToken`. Si arrancaste con un `--data-dir` distinto,
el archivo está en `<data-dir>/config.json`. Si el coordinador corre como
servicio systemd lanzado con `sudo`, la herramienta resuelve el home del
usuario original vía `SUDO_USER`, así que sigue estando bajo
`~/.fleet/coordinator/`.

También aparece en el banner que imprime `fleet coordinator` al arrancar; si se
ejecuta como servicio, puedes verlo con:

```bash
journalctl -u fleet-coordinator --no-pager | grep -i token
```

### ¿Puedo fijar yo el token en lugar de uno aleatorio?

Sí, pásalo en el primer arranque o en la instalación:

```bash
fleet coordinator --token mi-token-secreto
fleet coordinator install --token mi-token-secreto
```

Quedará persistido en `config.json` y será el que esperen los workers.

### ¿Y el secreto JWT? ¿Hay que rotarlo?

Se autogenera y se guarda junto al token (`jwtSecret` en `config.json`). Si lo
rotas (`--jwt-secret <nuevo>`), todos los tokens JWT emitidos previamente
quedan invalidados; los workers se re-autenticarán automáticamente con su
secreto persistente al siguiente latido.

### ¿Cuál es el usuario y contraseña por defecto del panel?

`admin` / `admin`. Cámbialo con:

```bash
fleet coordinator --admin-password <nueva-contraseña>
# o, si está como servicio:
sudo fleet coordinator install --admin-password <nueva-contraseña>
```

### ¿Dónde se guardan las credenciales del worker?

En `~/.fleet/worker-{nombre}/worker.json`. Contiene el ID y el secreto que el
worker usa para reautenticarse contra el coordinador sin volver a presentar el
token de registro. **Hacer backup** de este archivo equivale a "trasladar" el
worker.

---

## Workers y descubrimiento

### Tengo el coordinador y el worker en la **misma máquina**. ¿Necesito pasar URL y token?

No. `fleet worker` lee `~/.fleet/coordinator/config.json` (o la unidad systemd
del coordinador) y se conecta a `http://localhost:<puerto>` con el token ya
encontrado en disco.

### Y en la **misma LAN** pero distintas máquinas?

El coordinador anuncia su URL por **mDNS**. El worker la descubre solo; lo
único que tienes que pasar la primera vez es `--token`:

```bash
fleet worker --token <token>
```

Por seguridad el token **no** se publica por mDNS.

### ¿Y si hay varios coordinadores en la red?

`fleet worker` los lista y te pide elegir con `--coordinator <url>`.

### Mi red bloquea multicast / no quiero usar mDNS

- Arranca el coordinador con `--no-mdns` para no anunciar nada.
- Arranca los workers con `--no-discover` y pasa `--coordinator` y `--token`
  explícitamente.

### ¿Puedo correr varios workers en el mismo host?

Sí, dales nombres distintos con `--name`:

```bash
fleet worker --name build-01
fleet worker --name build-02
```

Cada uno tendrá su propio `~/.fleet/worker-{name}/` y, si los instalas como
servicios, se llamarán `fleet-worker-build-01`, `fleet-worker-build-02`, etc.

### ¿Cada cuánto sondea el worker la cola?

Cada 10 s por defecto. Ajustable con `--poll-interval <segundos>`.

---

## Servicios systemd

### ¿Cómo se llaman los servicios?

| Componente | Nombre del servicio       |
|------------|---------------------------|
| Coordinador| `fleet-coordinator`       |
| Worker     | `fleet-worker-{nombre}`   |

Las unidades viven en `/etc/systemd/system/` y corren bajo el usuario que
invocó el `install` (resuelto vía `SUDO_USER`).

### ¿Por qué se me pide la contraseña de `sudo` aunque no la haya escrito?

Los subcomandos `install`, `uninstall` y `update` se **autoelevan** vía
`SudoElevation.ReExecAsRootIfNeeded()`. Reejecutan la propia tool con `sudo`
preservando `PATH`, `DOTNET_ROOT` y `HOME` para que las instalaciones
per-usuario de .NET sigan funcionando.

### El autoelevación falla en mi entorno (sudoers personalizado, etc.)

Llama tú a `sudo` explícitamente; la herramienta detecta que ya es root y se
salta el re-exec:

```bash
sudo dnx dotnetfleet.tool coordinator install --port 5000
sudo fleet coordinator install --port 5000
```

### Comandos útiles para gestionarlos

```bash
sudo systemctl status fleet-coordinator
sudo systemctl restart fleet-worker-build-01
journalctl -u fleet-coordinator -f
fleet coordinator uninstall          # desinstala el servicio
```

---

## Datos, almacenamiento y caché

### ¿Dónde se guarda todo?

| Componente  | Contenido                                                     | Ruta por defecto                |
|-------------|---------------------------------------------------------------|---------------------------------|
| Coordinador | SQLite (proyectos, jobs, historial), `config.json`            | `~/.fleet/coordinator/`         |
| Worker      | `worker.json` (id + secreto), repos clonados, caché LRU       | `~/.fleet/worker-{nombre}/`     |

Cambia la ubicación con `--data-dir`. Útil, por ejemplo, para mover la caché
de repos a un disco externo:

```bash
fleet worker --data-dir /mnt/external-drive/fleet/worker-build-01
```

### ¿Cómo se evita que el caché de repos llene el disco?

Cada worker aplica una política **LRU**: cuando el tamaño total supera el
límite, expulsa los repos menos usados. Ajusta el límite (en GB) con
`--max-disk` (10 GB por defecto).

### ¿Qué base de datos usa el coordinador? ¿Puedo cambiarla?

SQLite (un único fichero bajo el `data-dir` del coordinador). No hay soporte
para otras bases de datos hoy.

### ¿Cómo hago backup?

Copiar dos directorios basta:

- **Coordinador:** todo `~/.fleet/coordinator/` (DB + `config.json`).
- **Workers:** `~/.fleet/worker-{nombre}/worker.json` (las credenciales). El
  caché de repos no es crítico — se reconstruye solo.

### ¿Las migraciones de esquema son automáticas?

Sí. Al arrancar, el coordinador ejecuta `EnsureCreatedAsync` y aplica
migraciones manuales con `ALTER TABLE` protegidas por checks sobre
`pragma_table_info`. No hay que correr ningún comando externo.

---

## Actualizaciones y rollback

### ¿Cómo actualizo a una versión nueva?

Una sola orden actualiza la global tool **y** reinicia todos los servicios
fleet locales:

```bash
fleet update
```

Reejecuta con `sudo` automáticamente y preserva `PATH`/`DOTNET_ROOT`/`HOME`.

### ¿Tengo que reinstalar los servicios después de actualizar?

No. Las unidades systemd apuntan a `~/.dotnet/tools/fleet` (la global tool), que
queda en la misma ruta tras actualizar. Basta con `systemctl restart`, que es
lo que `fleet update` ya hace.

### ¿Pierdo proyectos, jobs o historial al actualizar?

No. La actualización **solo reemplaza el binario**. Todo lo que vive bajo
`--data-dir` (proyectos, jobs, historial, JWT secret, token, credenciales de
worker, caché de repos) se conserva.

### ¿Cómo hago rollback a una versión anterior?

```bash
dotnet tool update -g DotnetFleet.Tool --version <versión-anterior>
sudo systemctl restart fleet-coordinator
sudo systemctl restart fleet-worker-<nombre>
```

---

## Seguridad y red

### ¿Cómo autentica el worker contra el coordinador?

1. **Bootstrap:** primera petición con cabecera `X-Registration-Token` igual
   al `RegistrationToken` configurado.
2. El coordinador devuelve un **JWT** (`Role=Worker`) y un secreto persistente.
3. A partir de ahí, el worker se reautentica con su secreto local — el token
   de registro **no** se vuelve a usar.

### ¿Qué pasa si filtro el token de registro?

Cualquiera que lo tenga puede registrar workers. Mitigación: rota el token
arrancando el coordinador con `--token <nuevo>` y revoca los workers que no
reconozcas desde el panel.

### ¿Conviene exponer el coordinador a Internet?

No por defecto. No incluye TLS ni proxy inverso integrados. Si lo necesitas,
ponlo detrás de Nginx/Caddy/Traefik con HTTPS y restringe el acceso por IP o
VPN.

### ¿Puedo correrlo todo en una red privada sin Internet?

Sí, siempre que los repos a desplegar y NuGet estén accesibles. La tool no
requiere conexión saliente al fabricante.

---

## Resolución de problemas

### "no such column" al arrancar el coordinador tras una actualización

El esquema de la DB quedó desfasado. Comprueba que el binario que arranca es
el actualizado:

```bash
which fleet
fleet --version
sudo systemctl restart fleet-coordinator
```

Si persiste, revisa los logs del arranque (`journalctl -u fleet-coordinator
-n 200`) — la migración manual debería aplicarse sola en
`InitializeDatabaseAsync`.

### Un worker aparece "Online" pero los jobs antiguos siguen "Running"

Al recibir un `status=Online`, el coordinador marca como **Failed** los jobs
no terminales asignados a ese worker (se asume que un worker que se anuncia
como ocioso no puede tener jobs vivos — son restos de un crash). Es el
comportamiento esperado. Si ves un job estancado y el worker no se anuncia,
reinícialo:

```bash
sudo systemctl restart fleet-worker-<nombre>
```

### El worker dejó de procesar y veo `Abort` en los logs

`should-cancel` puede devolver `Continue`, `Cancel` o `Abort`. `Abort`
significa que el coordinador considera el job terminal/huérfano: el worker
**libera el job y no reporta finalización**. Es un mecanismo de
auto-recuperación, no un error.

### `fleet worker` no encuentra el coordinador en la LAN

- Asegúrate de que el coordinador no se arrancó con `--no-mdns`.
- Comprueba que el firewall permite tráfico multicast (UDP 5353).
- Como alternativa, pasa `--coordinator <url> --token <token>` explícitamente.

### Cambié el puerto del coordinador y los workers ya no se conectan

Si los workers usaron auto-descubrimiento local, releen el `config.json` /
unidad systemd y se actualizan solos. Si tenían `--coordinator` fijo, hay que
actualizarlo (o reinstalar el servicio del worker con la nueva URL).

### `sudo fleet ...` me dice `sudo: fleet: orden no encontrada`

`sudo` resetea el `PATH` al `secure_path` de `/etc/sudoers`, donde
`~/.dotnet/tools` no está incluido. Tres formas de arreglarlo:

1. **No pongas `sudo` tú — deja que la tool se autoeleve.** `fleet` se
   reejecuta con `sudo` por su ruta absoluta y preserva `PATH`, `DOTNET_ROOT`
   y `HOME`. Es lo que están pensados para hacer `install`, `uninstall` y
   `update`:
   ```bash
   fleet worker install -c http://192.168.1.29:5000 -t <token>
   ```
2. **Llama a la ruta absoluta:**
   ```bash
   sudo ~/.dotnet/tools/fleet worker install -c http://192.168.1.29:5000 -t <token>
   ```
3. **Fuerza el PATH:**
   ```bash
   sudo env "PATH=$PATH" fleet worker install -c http://192.168.1.29:5000 -t <token>
   ```

Si prefieres que `sudo fleet` funcione siempre, añade `~/.dotnet/tools` al
`secure_path` de `/etc/sudoers` (vía `sudo visudo`), pero normalmente no hace
falta porque la autoelevación hace todo el trabajo.

### `fleet worker install -c 192.168.1.29:5000 -t <token>` no funciona

Casi siempre es uno de estos tres motivos. Para un worker **local que apunta a
un coordinador remoto** la receta correcta es:

```bash
sudo fleet worker install \
  --coordinator http://192.168.1.29:5000 \
  --token xxwVtE66hkqLD2SVQ1PBYoj5yT2moQNl \
  --name $(hostname)
```

Comprueba:

1. **Falta el esquema en la URL.** `--coordinator` necesita un URI completo:
   `http://192.168.1.29:5000`, no `192.168.1.29:5000`. Sin `http://` la
   `HttpClient` del worker no puede construir la BaseAddress y falla al primer
   latido.
2. **`install` requiere `sudo`.** Crea la unidad en `/etc/systemd/system/`. La
   tool intenta autoelevarse, pero si el `sudo` no es interactivo (sesión SSH
   sin TTY, sudoers personalizados…), reejecuta tú: `sudo fleet worker install
   …`.
3. **El servicio se llama por el nombre del worker.** Si no pasas `--name`,
   usa el `hostname`. Verifica el nombre real con:
   ```bash
   systemctl list-units 'fleet-worker-*' --all
   journalctl -u fleet-worker-<nombre> -n 100 --no-pager
   ```
4. **El coordinador debe ser alcanzable desde el worker.** Pruébalo antes de
   instalar:
   ```bash
   curl -i http://192.168.1.29:5000/healthz
   ```
   Si da `Connection refused`, el coordinador está bindeado a `localhost` y no
   a `0.0.0.0`. Reinstálalo con `--urls http://0.0.0.0:5000` o pásale
   `ASPNETCORE_URLS`.
5. **El token debe ser exactamente el del `config.json` del coordinador**, sin
   espacios ni saltos. Confírmalo con:
   ```bash
   ssh user@192.168.1.29 'cat ~/.fleet/coordinator/config.json'
   ```

Si quieres probar primero **en primer plano** (sin instalar el servicio), usa
exactamente la misma línea sin `install`:

```bash
fleet worker --coordinator http://192.168.1.29:5000 --token <token>
```

Así ves los logs directamente en consola y aíslas si el problema es el
servicio o la conexión.

### ¿Cómo puedo abrir la UI de administración?

Apunta el navegador al puerto del coordinador (por defecto
`http://localhost:5000`) y entra con `admin` / `admin` (o la contraseña que
hayas fijado).
