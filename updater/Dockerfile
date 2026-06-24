FROM docker:27-cli
COPY plant-floor-updater.sh /usr/local/bin/plant-floor-updater.sh
RUN chmod +x /usr/local/bin/plant-floor-updater.sh
ENTRYPOINT ["/usr/local/bin/plant-floor-updater.sh"]
