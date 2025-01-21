# Container Suspender
A tool which acts as a reverse proxy to a containered application, with a bit of extra logic to PAUSE or STOP containers which are not currently being hit.

# Features
* Reverse proxy basically any kind of TCP or UDP request from LISTEN PORT to TARGET PORT at TARGET ADDRESS
* Keep an internal check of docker container state
* Pause/Stop containers after some time frame has occurred where no network activity has been seen going TO the container
* Unpause/Start containers immediately on network activity to them
	* Holding the TCP/UDP packets until the application has been confirmed to be healthy, if configured with healthcheck
* Interact on a whole docker-compose project, rather than just individual containers (turn off the database AS WELL as the app)

# Configuration
Configuration is handled by a .json file, an commented example of which is located [Here](blob/main/Configuration/config.example.json)

# Confirmed applications that work without issue
* Stable Diffusion WebUI / Forge / SD.Next
* Ollama
* Open WebUI (so long as it's configured to ALSO go through for underlying services like Ollama)
* Blinko
* Jellyseerr / Jellyfin / MStream / GameVault / FileBrowser
* minecraft-bedrock (UDP implementation)

# Invalid use-cases
If an application is expected to do stuff by itself (sonarr, radarr downloads), have a permanent connection to another service (discord bot) or 
generally not JUST be a user-facing frontend, then this will do nothing at best and be detrimental at worst.
* QBittorrent - Should always be running
* Watchtower - Because it needs to be running to get updates, and has no UI
* Wireguard VPN - This is already pretty damn lightweight, but handles other protocol types than TCP/UDP, and adding a layer on top is overkill
* Zoneminder - Cameras are always running, not just when you're looking
* Caddy - Lightweight, already a reverse proxy
	* You would want caddy to reverse proxy TO the suspender, then the suspender reverse proxy TO the application
	* Since Caddy handles SSL stuff