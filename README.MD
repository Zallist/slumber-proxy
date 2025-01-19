# Container Suspender
A tool which acts as a reverse proxy to a containered application, with a bit of extra logic to PAUSE or STOP containers which are not currently being hit.

# TODO
* Health Check on spinning back up so it doesn't have to wait an arbitrary time before forwarding requests
* Ability to DOCKER COMPOSE STOP/PAUSE entire docker compose stacks