FROM freqtradeorg/freqtrade:stable

USER root

RUN apt-get update && \
    apt-get install -y \
        git
		
USER ftuser

COPY ./.publish/* /freqtrade/

ENTRYPOINT ["/freqtrade/strategizer"]